using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Where a SmartLink account's tier is edited (the tiers plan, decision 8,
    /// and the brief's settled items 9, 10 and 11): offline, per account, in
    /// Manage SmartLink Accounts; the Network tab shows the in-use account's
    /// tier read-only; only an operator's choice marks a tier chosen.
    /// </summary>
    /// <remarks>
    /// Source-read, as <see cref="ConnectionChoiceDescriptionsTests"/> is,
    /// because this assembly cannot load WPF. Pressing the keys is the bench's.
    /// </remarks>
    public sealed class TierEditingMovedTests
    {
        private static string Src(params string[] parts) =>
            File.ReadAllText(Path.Combine(new[] { IntegrationPassTree.Root }.Concat(parts).ToArray()));

        [Fact]
        public void The_accounts_dialog_s_tier_callback_is_required_so_no_construction_site_can_leave_the_box_missing()
        {
            string dialog = Src("JJFlexWpf", "Dialogs", "SmartLinkAccountDialog.xaml.cs");
            Assert.Contains("public required Func<string, Radios.SmartLinkConnectionMode, bool> SetConnectionTier", dialog);
        }

        [Fact]
        public void Both_construction_sites_wire_the_tier_and_hand_the_dialog_each_account_s_tier()
        {
            string vb = Src("globals.vb");
            Assert.Contains(".SetConnectionTier = Function(email, tier) mgr.ChooseConnectionMode(email, tier)", vb);
            Assert.Contains(".ConnectionTier = a.ConnectionMode,", vb);
            Assert.Contains(".TierChosen = a.TierChosen", vb);

            string mw = Src("JJFlexWpf", "MainWindow.xaml.cs");
            Assert.Contains("SetConnectionTier = (email, tier) => mgr.ChooseConnectionMode(email, tier)", mw);
            Assert.Contains("ConnectionTier = a.ConnectionMode,", mw);
            Assert.Contains("TierChosen = a.TierChosen", mw);

            // ...and there are exactly two sites, so a third cannot appear unwired unnoticed.
            int sites = Regex.Matches(vb, @"New JJFlexWpf\.Dialogs\.SmartLinkAccountCallbacks\(").Count
                      + Regex.Matches(mw, @"new Dialogs\.SmartLinkAccountCallbacks\b").Count;
            Assert.Equal(2, sites);
        }

        [Fact]
        public void The_accounts_dialog_has_the_tier_box_and_still_closes_on_Escape()
        {
            string xaml = Src("JJFlexWpf", "Dialogs", "SmartLinkAccountDialog.xaml");
            Assert.Contains("x:Name=\"TierCombo\"", xaml);
            Assert.Contains("SelectionChanged=\"TierCombo_SelectionChanged\"", xaml);
            Assert.Contains("IsCancel=\"True\"", xaml);
            Assert.Contains("local:JJFlexHelp.Text=", xaml.Substring(xaml.IndexOf("x:Name=\"TierCombo\"", StringComparison.Ordinal)));
        }

        [Fact]
        public void The_Network_tab_neither_gates_nor_saves_the_tier()
        {
            string settings = Src("JJFlexWpf", "Dialogs", "SettingsDialog.xaml.cs");
            Assert.DoesNotContain("SaveCurrentAccountConnectionMode(", settings);
            Assert.DoesNotContain("higherTiersValid", settings);
            Assert.DoesNotContain("void RecomputeConnectionModeAvailability(", settings);
            int show = settings.IndexOf("private void ShowTierInUse()", StringComparison.Ordinal);
            Assert.True(show > 0);
            string body = settings.Substring(show, 1200);
            Assert.Contains("Tier1Radio.IsEnabled = false;", body);
            Assert.Contains("Tier2Radio.IsEnabled = false;", body);
            Assert.Contains("Tier3Radio.IsEnabled = false;", body);
            Assert.Contains("settings.network.tier_in_use", body);
            Assert.Contains("x:Name=\"TierInUseText\"", Src("JJFlexWpf", "Dialogs", "SettingsDialog.xaml"));
        }

        [Fact]
        public void Saving_a_per_radio_choice_asks_whether_the_owning_account_would_refuse_it()
        {
            string profile = Src("JJFlexWpf", "Dialogs", "SettingsDialog.RadioProfile.cs");
            Assert.Contains("FlexBase.TierRefusalWarningFor(radioId, edit.Preference, disp)", profile);
        }
    }
}
