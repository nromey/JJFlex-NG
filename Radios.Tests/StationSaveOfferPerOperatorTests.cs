using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #1113, ruled by Noel 2026-11-12 08:05 (J3): the disconnect offer to save
    /// the setup to the radio is each operator's own setting, and the account
    /// value it used to be is carried, once, to the operators that existed at
    /// the upgrade (the #1106 rule: an operator added later starts at the
    /// default, off).
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class StationSaveOfferPerOperatorTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "jjflex-1113-" + Guid.NewGuid().ToString("N"));
        private readonly string _a = "A_" + Guid.NewGuid().ToString("N");
        private readonly string _b = "B_" + Guid.NewGuid().ToString("N");
        private readonly bool _savedAccount = StationSaveOffer.AccountValue;

        public StationSaveOfferPerOperatorTests()
        {
            Directory.CreateDirectory(_root);
            OperatorChange.ResetForTests();
        }

        public void Dispose()
        {
            OperatorChange.ResetForTests();
            StationSaveOffer.AccountValue = _savedAccount;
            string app = RadioConfig.AppDataRoot;
            foreach (string op in new[] { _a, _b })
                try { File.Delete(StationSaveOffer.OperatorFile(app, op)); } catch { }
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        private static string Src(string rel) => File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), rel));

        // ---- the setting follows the operator ----

        [Fact]
        public void One_operators_choice_is_not_another_operators()
        {
            string root = RadioConfig.AppDataRoot;
            OperatorChange.Establish(root, _a);
            FlexBase.OfferStationSaveOnDisconnect = true;
            Assert.True(FlexBase.OfferStationSaveOnDisconnect);

            OperatorChange.Announce(root, _b);
            Assert.False(FlexBase.OfferStationSaveOnDisconnect);

            OperatorChange.Announce(root, _a);
            Assert.True(FlexBase.OfferStationSaveOnDisconnect);
            Assert.True(File.Exists(StationSaveOffer.OperatorFile(root, _a)));
        }

        [Fact]
        public void An_operators_dont_ask_leaves_the_account_value_and_the_other_operator_alone()
        {
            string root = RadioConfig.AppDataRoot;
            StationSaveOffer.AccountValue = true;
            OperatorChange.Establish(root, _b);
            FlexBase.OfferStationSaveOnDisconnect = true;
            OperatorChange.Announce(root, _a);
            FlexBase.OfferStationSaveOnDisconnect = true;

            FlexBase.OfferStationSaveOnDisconnect = false;   // A: "Don't ask when I disconnect"

            Assert.False(FlexBase.OfferStationSaveOnDisconnect);
            Assert.True(StationSaveOffer.AccountValue);
            OperatorChange.Announce(root, _b);
            Assert.True(FlexBase.OfferStationSaveOnDisconnect);
        }

        [Fact]
        public void With_no_operator_the_account_value_is_the_one_in_use()
        {
            StationSaveOffer.AccountValue = true;
            Assert.True(FlexBase.OfferStationSaveOnDisconnect);
            FlexBase.OfferStationSaveOnDisconnect = false;
            Assert.False(StationSaveOffer.AccountValue);
        }

        [Fact]
        public void The_disconnect_gate_asks_the_operator_in_use()
        {
            string root = RadioConfig.AppDataRoot;
            FlexBase? rig = null;
            try
            {
                rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
                OperatorChange.Establish(root, _a);
                FlexBase.OfferStationSaveOnDisconnect = true;
                Assert.False(rig.ShouldOfferStationLayoutSave());
                Assert.DoesNotContain("switched off", rig.LastStationSaveOfferDecline);

                OperatorChange.Announce(root, _b);
                Assert.False(rig.ShouldOfferStationLayoutSave());
                Assert.Contains("switched off", rig.LastStationSaveOfferDecline);
            }
            finally { rig?.Dispose(); }
        }

        // ---- carried once, at the upgrade ----

        [Fact]
        public void Operators_listed_at_the_upgrade_start_from_the_account_value_and_later_ones_start_off()
        {
            Assert.True(StationSaveOffer.CarryOverAtUpgrade(_root, new[] { "A_op", "B_op" }, accountValue: true));
            Assert.True(StationSaveOffer.Read(_root, "A_op"));
            Assert.True(StationSaveOffer.Read(_root, "B_op"));
            Assert.True(File.Exists(StationSaveOffer.MarkerFile(_root)));

            // Added next year, or renamed (a new unique name): the default.
            Assert.False(StationSaveOffer.Read(_root, "C_new"));
            Assert.False(StationSaveOffer.CarryOverAtUpgrade(_root, new[] { "A_op", "C_new" }, accountValue: true));
            Assert.False(StationSaveOffer.Read(_root, "C_new"));
            Assert.False(File.Exists(StationSaveOffer.OperatorFile(_root, "C_new")));
        }

        [Fact]
        public void The_carry_over_keeps_an_operators_own_choice_and_runs_once()
        {
            StationSaveOffer.Write(_root, "A_op", false);
            Assert.True(StationSaveOffer.CarryOverAtUpgrade(_root, new[] { "A_op" }, accountValue: true));
            Assert.False(StationSaveOffer.Read(_root, "A_op"));

            StationSaveOffer.Write(_root, "A_op", true);
            Assert.False(StationSaveOffer.CarryOverAtUpgrade(_root, new[] { "A_op" }, accountValue: false));
            Assert.True(StationSaveOffer.Read(_root, "A_op"));
        }

        [Fact]
        public void An_unreadable_file_reads_as_off()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(StationSaveOffer.OperatorFile(_root, "A_op"), "{ not json");
            Assert.False(StationSaveOffer.Read(_root, "A_op"));
        }

        // ---- the application's wiring ----

        [Fact]
        public void Startup_carries_the_account_value_before_the_operator_is_recorded()
        {
            string src = Src("globals.vb");
            int carry = src.IndexOf("Radios.StationSaveOffer.CarryOverAtUpgrade(", StringComparison.Ordinal);
            int establish = src.IndexOf("Radios.OperatorChange.Establish(BaseConfigDir", StringComparison.Ordinal);
            Assert.True(carry > 0 && establish > carry, "the carry-over comes before the operator in use is recorded");
            Assert.Contains("JJFlexWpf.AudioOutputConfig.Load(BaseConfigDir).OfferStationSaveOnDisconnect", src);
        }

        /// <summary>
        /// The account file feeds the account value only: the startup load and
        /// Apply() must not write it over the operator's own setting (they did
        /// so through the static until #1113).
        /// </summary>
        [Fact]
        public void The_account_file_never_writes_the_operators_setting()
        {
            string main = Src("JJFlexWpf/MainWindow.xaml.cs");
            string config = Src("JJFlexWpf/AudioOutputConfig.cs");
            Assert.Contains("Radios.StationSaveOffer.AccountValue =\r\n                    userConfig.OfferStationSaveOnDisconnect;".Replace("\r\n", main.Contains("\r\n") ? "\r\n" : "\n"), main);
            Assert.Contains("Radios.StationSaveOffer.AccountValue = OfferStationSaveOnDisconnect;", config);
            Assert.DoesNotMatch(new Regex(@"FlexBase\.OfferStationSaveOnDisconnect\s*=\s*(userConfig\.)?OfferStationSaveOnDisconnect"), main + config);
        }

        [Fact]
        public void Settings_shows_and_saves_the_operators_own_setting()
        {
            string dlg = Src("JJFlexWpf/Dialogs/SettingsDialog.xaml.cs");
            Assert.Contains("OfferStationSaveOnDisconnectCheck.IsChecked = Radios.FlexBase.OfferStationSaveOnDisconnect;", dlg);
            Assert.Matches(new Regex(@"Radios\.FlexBase\.OfferStationSaveOnDisconnect =\s+OfferStationSaveOnDisconnectCheck\.IsChecked == true;"), dlg);
        }
    }
}
