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

        [Fact]
        public void The_cycle_key_reads_the_setting_and_the_old_fixed_list_is_gone()
        {
            string root = RepoRoot();
            string main = File.ReadAllText(Path.Combine(root, "JJFlexWpf", "MainWindow.xaml.cs"));
            Assert.DoesNotContain("CommonModes", main);
            int at = main.IndexOf("public void CycleMode(int direction)", StringComparison.Ordinal);
            Assert.True(at >= 0, "CycleMode is gone");
            string body = main.Substring(at, Math.Min(2500, main.Length - at));
            Assert.Contains("ModeCycle.FromSaved(Radios.AccessibilityConfig.Current.ModeCycleModes)", body);
            Assert.Contains("ModeCycle.KnownModes(RigControl.RadioModeList)", body);
            Assert.Contains("ModeCycle.Next(", body);
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
