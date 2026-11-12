using System;
using System.IO;
using System.Text.RegularExpressions;
using HamBands;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// With no radio since launch, Settings edits the operator's own license
    /// and saves it when it changed (settings are intents). It never saves an
    /// unchanged license, because the file's absence is how the first connect
    /// knows to ask for a license class; with no operator it names the change
    /// as not saved.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class LicenseWithoutRadioTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "jjflex-licnoradio-" + Guid.NewGuid().ToString("N"));
        private string RadiosFolder => Path.Combine(_root, "Radios");
        private const string Op = "K0TST_1";

        public LicenseWithoutRadioTests()
        {
            Directory.CreateDirectory(RadiosFolder);
            OperatorChange.ResetForTests();
        }

        public void Dispose()
        {
            OperatorChange.ResetForTests();
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        private string LicenseFile => Path.Combine(RadiosFolder, Op + "_licenseConfig.xml");

        [Fact]
        public void A_changed_license_is_saved_to_the_file_the_next_connect_reads()
        {
            new LicenseConfig { LicenseClass = Bands.Licenses.general }.Save(RadiosFolder, Op);
            OperatorChange.Establish(_root, Op);

            var edit = LicenseWithoutRadio.ForCurrentOperator();
            Assert.True(edit.HasAHome);
            Assert.Equal(Bands.Licenses.general, edit.License.LicenseClass);   // the operator's own, not the default

            edit.License.LicenseClass = Bands.Licenses.technition;
            edit.License.TxLockout = true;
            Assert.Equal(LicenseWithoutRadio.Outcome.Saved, edit.Commit());
            Assert.Null(LicenseWithoutRadio.CommitNote(LicenseWithoutRadio.Outcome.Saved));

            // Read back exactly as startup reads it at the next connect.
            var back = LicenseConfig.LoadOrCarry(RadiosFolder, _root, Op, out bool firstRun);
            Assert.False(firstRun);
            Assert.Equal(Bands.Licenses.technition, back.LicenseClass);
            Assert.True(back.TxLockout);
        }

        [Fact]
        public void An_unchanged_license_is_never_written_so_the_first_connect_still_asks_for_one()
        {
            Assert.False(File.Exists(LicenseFile));
            var edit = LicenseWithoutRadio.Open(_root, Op);
            Assert.True(edit.HasAHome);

            Assert.Equal(LicenseWithoutRadio.Outcome.Unchanged, edit.Commit());

            Assert.False(File.Exists(LicenseFile), "an unchanged license must not be written");
            Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
            LicenseConfig.LoadOrCarry(RadiosFolder, _root, Op, out bool firstRun);
            Assert.True(firstRun, "the first connect must still find no license and ask for one");
        }

        [Fact]
        public void A_second_commit_with_nothing_more_changed_writes_nothing_again()
        {
            var edit = LicenseWithoutRadio.Open(_root, Op);
            edit.License.LicenseClass = Bands.Licenses.general;
            Assert.Equal(LicenseWithoutRadio.Outcome.Saved, edit.Commit());   // Apply
            DateTime written = File.GetLastWriteTimeUtc(LicenseFile);
            File.SetLastWriteTimeUtc(LicenseFile, written.AddMinutes(-5));

            Assert.Equal(LicenseWithoutRadio.Outcome.Unchanged, edit.Commit());   // then OK
            Assert.Equal(written.AddMinutes(-5), File.GetLastWriteTimeUtc(LicenseFile));
        }

        [Fact]
        public void The_settings_roots_older_copy_is_read_and_a_change_is_saved_to_the_Radios_folder()
        {
            new LicenseConfig { LicenseClass = Bands.Licenses.advanced }.Save(_root, Op);

            var edit = LicenseWithoutRadio.Open(_root, Op);
            Assert.Equal(Bands.Licenses.advanced, edit.License.LicenseClass);
            edit.License.EnforceTxRules = false;
            Assert.Equal(LicenseWithoutRadio.Outcome.Saved, edit.Commit());

            Assert.True(File.Exists(LicenseFile));
            Assert.False(LicenseConfig.Load(RadiosFolder, Op).EnforceTxRules);
        }

        [Fact]
        public void With_no_operator_a_change_is_named_as_not_saved_and_nothing_is_written()
        {
            OperatorChange.Establish(_root, null);
            var edit = LicenseWithoutRadio.ForCurrentOperator();
            Assert.False(edit.HasAHome);

            Assert.Equal(LicenseWithoutRadio.Outcome.Unchanged, edit.Commit());   // nothing to say when nothing changed

            edit.License.LicenseClass = Bands.Licenses.general;
            var outcome = edit.Commit();
            Assert.Equal(LicenseWithoutRadio.Outcome.NotSaved, outcome);
            Assert.Equal(Lexicon.Get("settings.commit.license_not_saved"), LicenseWithoutRadio.CommitNote(outcome));
            Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
        }

        // ---- the wiring ----

        private static string Source(params string[] parts) =>
            File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), Path.Combine(parts))).Replace("\r\n", "\n");

        private static string Body(string src, string signature)
        {
            int at = src.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at > 0, signature + " not found");
            int end = src.IndexOf("\n    }\n", at, StringComparison.Ordinal);
            if (end < 0) end = src.IndexOf("\n        }\n", at, StringComparison.Ordinal);
            return src.Substring(at, end - at);
        }

        [Fact]
        public void Settings_with_no_Home_license_edits_and_commits_the_operators_own()
        {
            string menu = Body(Source("JJFlexWpf", "NativeMenuBar.cs"), "private void ShowSettingsDialog(");
            Assert.Matches(new Regex(@"var noRadioLicense = licenseConfig == null \? LicenseWithoutRadio\.ForCurrentOperator\(\) : null;\s*licenseConfig \?\?= noRadioLicense\?\.License;"), menu);
            int construct = menu.IndexOf("new Dialogs.SettingsDialog(", StringComparison.Ordinal);
            Assert.True(menu.IndexOf("licenseConfig ??= noRadioLicense?.License;", StringComparison.Ordinal) < construct,
                "the dialog must be built on the operator's license");
            Assert.Contains("dialog.NoRadioLicense = noRadioLicense;", menu);

            string dialog = Source("JJFlexWpf", "Dialogs", "SettingsDialog.xaml.cs");
            int apply = dialog.IndexOf("private bool ApplyAllSettings(bool closing)", StringComparison.Ordinal);
            int announce = dialog.IndexOf("var said = CommitAnnouncement(", apply, StringComparison.Ordinal);
            int commit = dialog.IndexOf("LicenseWithoutRadio.CommitNote(NoRadioLicense.Commit())", apply, StringComparison.Ordinal);
            Assert.InRange(commit, apply + 1, announce);
            Assert.Contains("if (licenseNote != null) failed.Add(licenseNote);", dialog.Substring(commit, announce - commit));
        }
    }
}
