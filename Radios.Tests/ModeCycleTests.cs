#nullable enable
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The mode cycle is a setting: which modes Alt+M and Alt+Shift+M visit,
    /// in the operator's order, and from any mode a press goes to the nearest
    /// mode in the cycle (ruled 2026-08-26 and 2026-10-19).
    /// </summary>
    // AccessibilityConfig.Load records where the operator file lives, so
    // the classes that call it share this collection rather than racing.
    [Collection(RadioConfigStaticsCollection.Name)]
    public class ModeCycleTests
    {
        private static readonly string[] All = ModeCycle.FlexLibModes.ToArray();

        [Fact]
        public void The_default_is_todays_seven_in_todays_order_and_leaves_DSB_and_SAM_out()
        {
            Assert.Equal(new[] { "USB", "LSB", "CW", "DIGU", "DIGL", "AM", "FM" }, ModeCycle.FromSaved(null));
            Assert.False(ModeCycle.Contains(ModeCycle.DefaultCycle, "DSB"));
            Assert.False(ModeCycle.Contains(ModeCycle.DefaultCycle, "SAM"));
            // ...and both are offered.
            Assert.True(ModeCycle.Contains(ModeCycle.KnownModes(null), "DSB"));
            Assert.True(ModeCycle.Contains(ModeCycle.KnownModes(null), "SAM"));
        }

        [Fact]
        public void A_press_follows_the_operators_order_and_wraps()
        {
            var cycle = ModeCycle.FromSaved(new[] { "CW", "USB", "DIGU" });
            Assert.Equal("USB", ModeCycle.Next(cycle, All, "CW", +1));
            Assert.Equal("DIGU", ModeCycle.Next(cycle, All, "USB", +1));
            Assert.Equal("CW", ModeCycle.Next(cycle, All, "DIGU", +1));
            Assert.Equal("DIGU", ModeCycle.Next(cycle, All, "CW", -1));
            Assert.Equal("CW", ModeCycle.Next(cycle, All, "usb", -1));   // case folded
        }

        [Fact]
        public void From_SAM_the_default_cycle_advances_to_the_nearest_mode_and_does_not_jump_to_an_end()
        {
            // FlexLib's order: USB, DIGU, LSB, DIGL, CW, DSB, AM, SAM, FM.
            // After SAM comes FM; before it, AM. The old fixed list jumped to
            // its last entry going forward and its first going back.
            var cycle = ModeCycle.DefaultCycle;
            Assert.Equal("FM", ModeCycle.Next(cycle, All, "SAM", +1));
            Assert.Equal("AM", ModeCycle.Next(cycle, All, "SAM", -1));
            Assert.Equal("AM", ModeCycle.Next(cycle, All, "DSB", +1));
            Assert.Equal("CW", ModeCycle.Next(cycle, All, "DSB", -1));
        }

        [Fact]
        public void The_nearest_mode_wraps_around_the_full_list()
        {
            var cycle = ModeCycle.FromSaved(new[] { "LSB", "CW" });
            // From FM, the last in FlexLib's list, forward wraps to USB (out),
            // DIGU (out), then LSB.
            Assert.Equal("LSB", ModeCycle.Next(cycle, All, "FM", +1));
            Assert.Equal("CW", ModeCycle.Next(cycle, All, "FM", -1));
        }

        [Fact]
        public void A_mode_in_neither_list_goes_to_an_end_of_the_cycle()
        {
            var cycle = ModeCycle.FromSaved(new[] { "CW", "USB" });
            Assert.Equal("CW", ModeCycle.Next(cycle, All, "RTTY", +1));
            Assert.Equal("USB", ModeCycle.Next(cycle, All, "RTTY", -1));
            Assert.Equal("CW", ModeCycle.Next(cycle, All, null, +1));
        }

        [Fact]
        public void An_empty_cycle_has_nowhere_to_go_and_one_mode_stays_put()
        {
            var empty = ModeCycle.FromSaved(Array.Empty<string>());
            Assert.Empty(empty);
            Assert.Null(ModeCycle.Next(empty, All, "USB", +1));

            var one = ModeCycle.FromSaved(new[] { "cw" });
            Assert.Equal("CW", ModeCycle.Next(one, All, "CW", +1));   // the caller says so, writes nothing
            Assert.Equal("CW", ModeCycle.Next(one, All, "USB", -1));  // from elsewhere it goes there
        }

        [Fact]
        public void The_radios_own_list_wins_when_it_has_one()
        {
            var radio = new[] { "lsb", "USB", "AM", "CW", "DIGL", "DIGU", "SAM", "FM", "NFM", "DFM", "RTTY", "USB" };
            var known = ModeCycle.KnownModes(radio);
            Assert.Equal(new[] { "LSB", "USB", "AM", "CW", "DIGL", "DIGU", "SAM", "FM", "NFM", "DFM", "RTTY" }, known);
            Assert.Equal(ModeCycle.FlexLibModes, ModeCycle.KnownModes(Array.Empty<string>()));
            // RTTY is now in the full list, so from RTTY the default cycle
            // moves to the nearest included mode after it, wrapping to LSB.
            Assert.Equal("LSB", ModeCycle.Next(ModeCycle.DefaultCycle, known, "RTTY", +1));
        }

        [Fact]
        public void The_cycle_is_kept_in_the_operators_file_and_survives_a_save_and_reload()
        {
            string dir = Path.Combine(Path.GetTempPath(), "jjflex-modecycle-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var before = AccessibilityConfig.Current;
            try
            {
                // Never set: no element, and the default cycle.
                var fresh = AccessibilityConfig.Load(dir, "op");
                Assert.Null(fresh.ModeCycleModes);
                fresh.Save(dir, "op");
                Assert.Null(AccessibilityConfig.Load(dir, "op").ModeCycleModes);

                var chosen = AccessibilityConfig.Load(dir, "op");
                chosen.ModeCycleModes = new[] { "CW", "SAM", "USB" };
                chosen.Save(dir, "op");
                Assert.Equal(new[] { "CW", "SAM", "USB" }, AccessibilityConfig.Load(dir, "op").ModeCycleModes);

                // Emptied is a choice, and stays empty rather than becoming the default.
                var emptied = AccessibilityConfig.Load(dir, "op");
                emptied.ModeCycleModes = Array.Empty<string>();
                emptied.Save(dir, "op");
                var reloaded = AccessibilityConfig.Load(dir, "op").ModeCycleModes;
                Assert.NotNull(reloaded);
                Assert.Empty(ModeCycle.FromSaved(reloaded));
            }
            finally
            {
                typeof(AccessibilityConfig).GetProperty(nameof(AccessibilityConfig.Current))!
                    .SetValue(null, before);
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>
        /// Codex B1, 2026-11-07: the cycle has to reach the file the
        /// application loads, not only the one the Settings dialog saves to.
        /// The application loads <c>AccessibilityConfig</c> from the settings
        /// root (<c>ApplicationEvents.vb</c>, with <c>BaseConfigDir</c>), while
        /// the dialog saves it under <c>OpenParms.ConfigDirectory</c>, which is
        /// the <c>Radios</c> folder beneath it. Both choices are read from the
        /// source first, so this test runs the directories they really use.
        /// </summary>
        [Fact]
        public void A_cycle_chosen_in_Settings_is_there_after_a_restart_through_the_real_folders()
        {
            string root = RepoRoot();
            string events = File.ReadAllText(Path.Combine(root, "ApplicationEvents.vb"));
            string globals = File.ReadAllText(Path.Combine(root, "globals.vb"));
            string menu = File.ReadAllText(Path.Combine(root, "JJFlexWpf", "NativeMenuBar.cs"));
            Assert.Contains("Radios.AccessibilityConfig.Load(BaseConfigDir, opName)", events);
            Assert.Contains(@"OpenParms.ConfigDirectory = BaseConfigDir & ""\Radios""", globals);
            Assert.Contains("dialog.ConfigDirectory = _window.OpenParms.ConfigDirectory;", menu);

            string baseConfigDir = Path.Combine(Path.GetTempPath(), "jjflex-modecycle-path-" + Guid.NewGuid().ToString("N"));
            string radiosDir = Path.Combine(baseConfigDir, "Radios");
            Directory.CreateDirectory(radiosDir);
            var before = AccessibilityConfig.Current;
            try
            {
                // An earlier session left the other preferences in the root file.
                new AccessibilityConfig { SliceArrowOrder = SliceArrowOrder.BottomToTop }.Save(baseConfigDir, "op");

                // This session: the load at the first connect, then Settings OK,
                // whose own save goes to the Radios folder under another name.
                AccessibilityConfig.ForgetHomeForTests();
                AccessibilityConfig.Load(baseConfigDir, "op");
                AccessibilityConfig.ChooseModeCycle(new[] { "CW", "USB" });
                AccessibilityConfig.Current.Save(radiosDir, "op_basename");

                // Restart.
                AccessibilityConfig.ForgetHomeForTests();
                var next = AccessibilityConfig.Load(baseConfigDir, "op");
                Assert.Equal(new[] { "CW", "USB" }, next.ModeCycleModes);
                Assert.Equal(SliceArrowOrder.BottomToTop, next.SliceArrowOrder);   // the rest left as it was
            }
            finally
            {
                AccessibilityConfig.ForgetHomeForTests();
                typeof(AccessibilityConfig).GetProperty(nameof(AccessibilityConfig.Current))!.SetValue(null, before);
                try { Directory.Delete(baseConfigDir, true); } catch { }
            }
        }

        /// <summary>
        /// Codex, recheck of 78b6c1fa: start without connecting, choose a
        /// cycle in Settings, exit before ever connecting, restart, connect:
        /// the earlier cycle came back, because the choice was held in memory
        /// for a load that only the first connect ran. The operator file is
        /// now loaded at launch (globals.vb InitializeApplication, before the
        /// initial connect), so the choice writes straight to it. This pins
        /// that launch load from source and then runs the sequence.
        /// </summary>
        [Fact]
        public void A_cycle_chosen_offline_survives_exiting_before_any_connect()
        {
            string globals = File.ReadAllText(Path.Combine(RepoRoot(), "globals.vb"));
            int init = globals.IndexOf("Friend Sub InitializeApplication()", StringComparison.Ordinal);
            Assert.True(init >= 0, "InitializeApplication has moved; re-check where the launch load sits");
            int open = globals.IndexOf("openTheRadio(True)", init, StringComparison.Ordinal);
            int load = globals.IndexOf("Radios.AccessibilityConfig.Load(BaseConfigDir, PersonalData.UniqueOpName(CurrentOp))",
                                       init, StringComparison.Ordinal);
            Assert.True(load > init && load < open, "the operator file is not loaded at launch, before the first connect");

            string baseConfigDir = Path.Combine(Path.GetTempPath(), "jjflex-modecycle-offline-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(baseConfigDir);
            var before = AccessibilityConfig.Current;
            try
            {
                // An earlier session saved a cycle.
                new AccessibilityConfig { ModeCycleModes = new[] { "CW", "USB" } }.Save(baseConfigDir, "op");

                // Launch: the launch load, which also lets the page show the
                // saved cycle before any connect.
                AccessibilityConfig.ForgetHomeForTests();
                Assert.Equal(new[] { "CW", "USB" }, AccessibilityConfig.Load(baseConfigDir, "op").ModeCycleModes);

                // Settings OK with no radio, then exit without connecting.
                AccessibilityConfig.ChooseModeCycle(new[] { "CW", "USB", "SAM" });
                AccessibilityConfig.ForgetHomeForTests();

                // Restart: the launch load, then the first connect's load.
                Assert.Equal(new[] { "CW", "USB", "SAM" }, AccessibilityConfig.Load(baseConfigDir, "op").ModeCycleModes);
                Assert.Equal(new[] { "CW", "USB", "SAM" }, AccessibilityConfig.Load(baseConfigDir, "op").ModeCycleModes);
            }
            finally
            {
                AccessibilityConfig.ForgetHomeForTests();
                typeof(AccessibilityConfig).GetProperty(nameof(AccessibilityConfig.Current))!.SetValue(null, before);
                try { Directory.Delete(baseConfigDir, true); } catch { }
            }
        }

        [Fact]
        public void A_cycle_chosen_before_the_first_connect_survives_that_connect_and_a_restart()
        {
            string baseConfigDir = Path.Combine(Path.GetTempPath(), "jjflex-modecycle-early-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(baseConfigDir);
            var before = AccessibilityConfig.Current;
            try
            {
                // A cycle already saved by an earlier session.
                new AccessibilityConfig { ModeCycleModes = new[] { "LSB" } }.Save(baseConfigDir, "op");

                // Settings OK before the file has been read this session.
                AccessibilityConfig.ForgetHomeForTests();
                AccessibilityConfig.ChooseModeCycle(new[] { "AM", "FM" });

                // The first connect reads the file: the new choice wins.
                Assert.Equal(new[] { "AM", "FM" }, AccessibilityConfig.Load(baseConfigDir, "op").ModeCycleModes);
                Assert.Equal(new[] { "AM", "FM" }, AccessibilityConfig.Current.ModeCycleModes);

                // And it was written, so a restart keeps it.
                AccessibilityConfig.ForgetHomeForTests();
                Assert.Equal(new[] { "AM", "FM" }, AccessibilityConfig.Load(baseConfigDir, "op").ModeCycleModes);
            }
            finally
            {
                AccessibilityConfig.ForgetHomeForTests();
                typeof(AccessibilityConfig).GetProperty(nameof(AccessibilityConfig.Current))!.SetValue(null, before);
                try { Directory.Delete(baseConfigDir, true); } catch { }
            }
        }

        /// <summary>
        /// The settings page works out which modes the Mode menu reaches from
        /// <see cref="RigCaps.ModeTable"/>; that is true only while the menu is
        /// built from the same list. And no mode cycle sentence promises a key
        /// of its own, because only seven modes have one (Codex B2 on 908aa3da).
        /// </summary>
        [Fact]
        public void The_menu_is_built_from_the_list_the_page_reads_and_no_sentence_promises_a_key()
        {
            string menu = File.ReadAllText(Path.Combine(RepoRoot(), "JJFlexWpf", "NativeMenuBar.cs"));
            int at = menu.IndexOf("var modeSub = AddSubmenu(slice, \"Mode\");", StringComparison.Ordinal);
            Assert.True(at >= 0, "the Mode submenu has moved; re-check what the settings page claims about it");
            Assert.Contains("foreach (string modeName in RigCaps.ModeTable)", menu.Substring(at, 200));

            foreach (string key in new[] { "settings.mode_cycle.intro", "settings.mode_cycle.list_help",
                                           "settings.mode_cycle.menu_note_all", "settings.mode_cycle.menu_note_except",
                                           "settings.mode_cycle.menu_note_except_many" })
            {
                string text = Lexicon.Get(key);
                Assert.NotEqual(key, text);
                Assert.DoesNotContain("own key", text, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void The_cycle_key_reads_the_setting_and_the_old_fixed_list_is_gone()
        {
            string root = RepoRoot();
            string main = File.ReadAllText(Path.Combine(root, "JJFlexWpf", "MainWindow.xaml.cs"));
            Assert.DoesNotContain("CommonModes", main);
            int at = main.IndexOf("public void CycleMode(int direction)", StringComparison.Ordinal);
            Assert.True(at >= 0, "CycleMode is gone");
            int end = main.IndexOf("public void SetMode(string mode)", at, StringComparison.Ordinal);
            string body = main.Substring(at, end - at);
            Assert.Contains("ModeCycle.FromSaved(Radios.AccessibilityConfig.Current.ModeCycleModes)", body);
            Assert.Contains("var radioModes = RigControl.RadioModeList;", body);
            Assert.Contains("var cycle = Radios.ModeCycle.OnRadio(saved, radioModes);", body);
            Assert.Contains("ModeCycle.KnownModes(radioModes)", body);
            Assert.Contains("ModeCycle.Next(cycle, allModes, currentMode, direction)", body);
            // The mode written and the mode spoken are the one Next returned
            // from the radio's own list, and nothing else.
            Assert.Contains("RigControl.Mode = newMode;", body);
            Assert.Contains("Radios.ScreenReaderOutput.Speak(newMode, VerbosityLevel.Terse, true);", body);
            Assert.Contains("Radios.ModeCycle.OnlyOneSentenceKey(saved, cycle)", body);
        }

        /// <summary>
        /// Codex B2 on 4ffb2106: a saved cycle of USB and DSB on the 8600
        /// (no DSB) leaves one mode on this radio, and Alt+M on USB said "USB
        /// is the only mode in the cycle" while the cycle held two. It now says
        /// USB is the only one in the cycle that this radio has.
        /// </summary>
        [Fact]
        public void One_mode_left_on_this_radio_is_called_the_only_one_this_radio_has()
        {
            var radio8600 = new[] { "LSB", "USB", "AM", "CW", "DIGL", "DIGU", "SAM", "FM", "NFM", "DFM", "RTTY" };
            var saved = ModeCycle.FromSaved(new[] { "USB", "DSB" });
            var onRadio = ModeCycle.OnRadio(saved, radio8600);
            Assert.Equal(new[] { "USB" }, onRadio);
            Assert.Equal("USB", ModeCycle.Next(onRadio, ModeCycle.KnownModes(radio8600), "USB", +1));   // stays put

            string sentence = Lexicon.Get(ModeCycle.OnlyOneSentenceKey(saved, onRadio), ("mode", "USB"));
            Assert.Equal("USB is the only mode in the cycle that this radio has.", sentence);

            // The control: a cycle that really holds one mode keeps the plain sentence.
            var single = ModeCycle.FromSaved(new[] { "USB" });
            Assert.Equal("USB is the only mode in the cycle.",
                Lexicon.Get(ModeCycle.OnlyOneSentenceKey(single, ModeCycle.OnRadio(single, radio8600)), ("mode", "USB")));
        }

        /// <summary>
        /// Found on the 8600 at the bench, 2026-11-07: its mode list has no
        /// DSB. With DSB in the cycle, Alt+M from SAM wrote DSB, the radio
        /// answered DIGU, and the app said "DSB". On a radio that reported its
        /// list, the cycle skips a mode it does not list, every mode the key
        /// writes and says is one the radio listed, and the saved cycle keeps
        /// the mode for a radio that has it.
        /// </summary>
        [Fact]
        public void On_a_radio_without_DSB_the_cycle_skips_it_and_the_saved_cycle_keeps_it()
        {
            var radio8600 = new[] { "LSB", "USB", "AM", "CW", "DIGL", "DIGU", "SAM", "FM", "NFM", "DFM", "RTTY" };
            var saved = ModeCycle.FromSaved(new[] { "USB", "LSB", "CW", "DIGU", "DIGL", "AM", "FM", "SAM", "DSB" });

            var onRadio = ModeCycle.OnRadio(saved, radio8600);
            Assert.Equal(new[] { "USB", "LSB", "CW", "DIGU", "DIGL", "AM", "FM", "SAM" }, onRadio);
            Assert.Contains("DSB", saved);   // the saved cycle itself is untouched

            var all = ModeCycle.KnownModes(radio8600);
            Assert.Equal("USB", ModeCycle.Next(onRadio, all, "SAM", +1));   // the bench's press: not DSB
            Assert.Equal("SAM", ModeCycle.Next(onRadio, all, "USB", -1));

            // Every press, from every mode the radio has, in both directions,
            // lands on a mode the radio listed.
            foreach (string from in radio8600)
                foreach (int dir in new[] { +1, -1 })
                    Assert.Contains(ModeCycle.Next(onRadio, all, from, dir)!, radio8600);

            // No list reported yet: the saved cycle unchanged, DSB and all.
            Assert.Same(saved, ModeCycle.OnRadio(saved, Array.Empty<string>()));
            Assert.Same(saved, ModeCycle.OnRadio(saved, null));

            // A cycle of only modes the radio lacks has nowhere to go here.
            Assert.Empty(ModeCycle.OnRadio(ModeCycle.FromSaved(new[] { "DSB" }), radio8600));
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
