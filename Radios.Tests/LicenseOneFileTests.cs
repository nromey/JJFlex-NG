using System;
using System.IO;
using System.Linq;
using HamBands;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #1072 repair, Codex's B2: the operator's license is loaded, tested for
    /// and saved in ONE file, the per-radio settings folder Settings has always
    /// saved to. Home used to load and seed a copy at the settings root, so a
    /// transmit lockout chosen in Settings came back off after switching away
    /// and back, and the next Settings save wrote that over the choice.
    /// </summary>
    public sealed class LicenseOneFileTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "jjflex-1072-license-" + Guid.NewGuid().ToString("N"));
        private string Radios => Path.Combine(_root, "Radios");

        public LicenseOneFileTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        /// <summary>What Home does at startup and at every operator change.</summary>
        private LicenseConfig HomeLoads(string op, out bool firstRun)
        {
            var license = LicenseConfig.LoadOrCarry(Radios, _root, op, out firstRun);
            if (firstRun) license.Save(Radios, op);
            return license;
        }

        /// <summary>What Settings' Apply does: the held object, saved to OpenParms.ConfigDirectory.</summary>
        private void SettingsSaves(LicenseConfig held, string op) => held.Save(Radios, op);

        [Fact]
        public void Switching_away_and_back_keeps_the_transmit_lockout_Settings_saved()
        {
            // B's first run seeds defaults, and B then turns on the lockout in Settings.
            var b = HomeLoads("B", out bool bFirst);
            Assert.True(bFirst);
            b.TxLockout = true;
            b.LicenseClass = Bands.Licenses.general;
            SettingsSaves(b, "B");

            // Switch to A, then back to B, without disconnecting.
            HomeLoads("A", out _);
            var back = HomeLoads("B", out bool again);

            Assert.False(again);
            Assert.True(back.TxLockout, "B's transmit lockout came back off after switching away and back");
            Assert.Equal(Bands.Licenses.general, back.LicenseClass);

            // And the next Settings save keeps it.
            SettingsSaves(back, "B");
            Assert.True(LicenseConfig.Load(Radios, "B").TxLockout);
        }

        [Fact]
        public void Where_both_copies_exist_the_one_Settings_wrote_wins_and_neither_is_touched()
        {
            // The pre-repair state: a root copy of first-run defaults beside Settings' choice.
            new LicenseConfig().Save(_root, "B");
            new LicenseConfig { TxLockout = true, LicenseClass = Bands.Licenses.advanced }.Save(Radios, "B");
            string rootFile = Path.Combine(_root, "B_licenseConfig.xml");
            string rootBefore = File.ReadAllText(rootFile);

            var loaded = HomeLoads("B", out bool firstRun);

            Assert.False(firstRun);
            Assert.True(loaded.TxLockout);
            Assert.Equal(Bands.Licenses.advanced, loaded.LicenseClass);
            Assert.Equal(rootBefore, File.ReadAllText(rootFile));
        }

        [Fact]
        public void A_root_copy_alone_is_carried_and_never_counted_a_first_run()
        {
            new LicenseConfig { LicenseClass = Bands.Licenses.technition }.Save(_root, "B");
            string rootFile = Path.Combine(_root, "B_licenseConfig.xml");
            string rootBefore = File.ReadAllText(rootFile);

            var loaded = HomeLoads("B", out bool firstRun);

            Assert.False(firstRun);
            Assert.Equal(Bands.Licenses.technition, loaded.LicenseClass);
            Assert.False(File.Exists(Path.Combine(Radios, "B_licenseConfig.xml")),
                "a carried root copy was written into the Radios folder by a load");
            Assert.Equal(rootBefore, File.ReadAllText(rootFile));
        }

        [Fact]
        public void First_run_defaults_go_to_the_one_file_and_only_when_there_is_no_copy_anywhere()
        {
            var loaded = HomeLoads("B", out bool firstRun);
            Assert.True(firstRun);
            Assert.True(File.Exists(Path.Combine(Radios, "B_licenseConfig.xml")));
            Assert.False(File.Exists(Path.Combine(_root, "B_licenseConfig.xml")));

            HomeLoads("B", out bool second);
            Assert.False(second);
        }

        /// <summary>
        /// The callers' own directories, which is where the defect lived: a
        /// test of the store with one directory argument cannot see it.
        /// </summary>
        [Fact]
        public void Home_and_Settings_name_the_same_folder()
        {
            string repo = FieldKeyMapScan.RepoRoot();
            string globals = File.ReadAllText(Path.Combine(repo, "globals.vb"));
            Assert.Contains("OpenParms.ConfigDirectory = BaseConfigDir & \"\\Radios\"", globals);

            string events = File.ReadAllText(Path.Combine(repo, "ApplicationEvents.vb")).Replace("\r\n", "\n");
            Assert.Contains("Radios.LicenseConfig.LoadOrCarry(\n                            BaseConfigDir & \"\\Radios\", BaseConfigDir, opName, isFirstRun)", events);
            Assert.Contains("handlers.License.Save(BaseConfigDir & \"\\Radios\", opName)", events);

            string main = File.ReadAllText(Path.Combine(repo, "JJFlexWpf", "MainWindow.xaml.cs"));
            Assert.Contains("_freqOutHandlers.License.Save(OpenParms.ConfigDirectory, OpenParms.GetOperatorName());", main);

            // No reader, test or writer of the license names any other folder.
            var vb = Directory.GetFiles(repo, "*.vb", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                         && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                .Select(File.ReadAllText);
            string all = string.Join("\n", vb);
            Assert.DoesNotContain("LicenseConfig.Load(BaseConfigDir,", all);
            Assert.DoesNotContain("LicenseConfig.Exists(BaseConfigDir,", all);
            Assert.DoesNotContain("License.Save(BaseConfigDir,", all);
        }
    }
}
