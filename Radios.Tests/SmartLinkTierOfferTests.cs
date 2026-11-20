#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Radios.SmartLink;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The one-time tier offer and the "chosen" flag it reads and writes (the
    /// tiers plan's decisions 6 and 7, and the brief's settled items 2 and 3).
    /// </summary>
    /// <remarks>
    /// No test here saves SmartLink accounts: <c>SmartLinkAccountManager</c>
    /// writes the operator's real SmartLinkAccounts.json through a path a test
    /// cannot move. The flag is round-tripped through <c>StoredAccount</c> and
    /// <c>JsonSerializer</c> in memory, as <see cref="SmartLinkAccountPortTests"/> does.
    /// </remarks>
    public sealed class SmartLinkTierOfferTests
    {
        private static RadioPortReport Report(bool manual, bool upnp) => new(
            new AdvertisedPortPair(PortPairSource.Manual, manual ? 4994 : -1, manual ? 4993 : -1, true),
            new AdvertisedPortPair(PortPairSource.Upnp, upnp ? 21000 : -1, upnp ? 21001 : -1, upnp), !manual && !upnp, true);

        private static SmartLinkConnectRecord Planned(SmartLinkConnectionMode tier, bool chosen, RadioConnectionPreference choice, RadioPortReport report)
        {
            var input = new SmartLinkPolicyInput(tier, chosen, choice, false, report);
            return SmartLinkConnectRecord.Begin("4711-1111-2222-3333", "owner@example.invalid", input, SmartLinkMethodPolicy.Decide(input), DateTime.UtcNow);
        }

        [Fact]
        public void A_UPnP_only_radio_under_Tier_1_is_offered_Tier_2_and_Yes_saves_Tier_2()
        {
            var offer = SmartLinkTierOffers.For(Planned(SmartLinkConnectionMode.ManualPortForwardOnly, false, RadioConnectionPreference.Auto, Report(false, true)), accountIsSaved: true);
            Assert.NotNull(offer);
            Assert.Equal(SmartLinkConnectionMode.ManualPlusUpnp, offer!.OfferedTier);
            Assert.Equal("owner@example.invalid", offer.AccountEmail);
            Assert.Equal(SmartLinkConnectionMode.ManualPlusUpnp, SmartLinkTierOffers.TierOnYes(offer));
        }

        [Fact]
        public void A_radio_with_no_ports_under_Tier_1_or_2_is_offered_Tier_3()
        {
            foreach (var tier in new[] { SmartLinkConnectionMode.ManualPortForwardOnly, SmartLinkConnectionMode.ManualPlusUpnp })
            {
                var offer = SmartLinkTierOffers.For(Planned(tier, false, RadioConnectionPreference.Auto, Report(false, false)), accountIsSaved: true);
                Assert.Equal(SmartLinkConnectionMode.AutomaticHolePunch, offer!.OfferedTier);
            }
        }

        [Fact]
        public void No_saves_Tier_1()
        {
            Assert.Equal(SmartLinkConnectionMode.ManualPortForwardOnly, SmartLinkTierOffers.TierOnNo);
        }

        [Fact]
        public void A_chosen_account_is_never_offered()
        {
            Assert.Null(SmartLinkTierOffers.For(Planned(SmartLinkConnectionMode.ManualPortForwardOnly, true, RadioConnectionPreference.Auto, Report(false, true)), accountIsSaved: true));
        }

        [Fact]
        public void An_account_not_saved_here_is_never_offered()
        {
            Assert.Null(SmartLinkTierOffers.For(Planned(SmartLinkConnectionMode.ManualPortForwardOnly, false, RadioConnectionPreference.Auto, Report(false, true)), accountIsSaved: false));
        }

        [Fact]
        public void Only_a_tier_refusal_earns_an_offer()
        {
            // Hole punch always under Tier 1: the radio needs nothing the tier forbids; the choice demands it.
            Assert.Null(SmartLinkTierOffers.For(Planned(SmartLinkConnectionMode.ManualPortForwardOnly, false, RadioConnectionPreference.HolePunch, Report(true, false)), true));
            // Forwarded ports only with nothing usable: no tier would help.
            Assert.Null(SmartLinkTierOffers.For(Planned(SmartLinkConnectionMode.ManualPortForwardOnly, false, RadioConnectionPreference.ForwardOnly, Report(false, false)), true));
            // Not refused at all.
            Assert.Null(SmartLinkTierOffers.For(Planned(SmartLinkConnectionMode.ManualPortForwardOnly, false, RadioConnectionPreference.Auto, Report(true, false)), true));
        }

        [Fact]
        public void The_offer_and_the_refusals_have_words_naming_the_radio_and_account_and_the_offer_says_every_radio()
        {
            var upnp = new SmartLinkTierOffer("4711-1111-2222-3333", "owner@example.invalid", SmartLinkConnectionMode.ManualPlusUpnp);
            var punch = upnp with { OfferedTier = SmartLinkConnectionMode.AutomaticHolePunch };
            foreach (var o in new[] { upnp, punch })
            {
                string q = SmartLinkTierOffers.Question(o, "Bench 8600");
                Assert.Contains("Bench 8600", q);
                Assert.Contains("owner@example.invalid", q);
                Assert.Contains("every radio on this account", q);
                Assert.DoesNotContain("{", q);
                Assert.DoesNotContain("DRAFT", q);
            }
            Assert.False(string.IsNullOrWhiteSpace(SmartLinkTierOffers.Title()));

            foreach (var rec in new[]
            {
                Planned(SmartLinkConnectionMode.ManualPortForwardOnly, false, RadioConnectionPreference.Auto, Report(false, true)),
                Planned(SmartLinkConnectionMode.ManualPortForwardOnly, false, RadioConnectionPreference.Auto, Report(false, false)),
                Planned(SmartLinkConnectionMode.ManualPortForwardOnly, false, RadioConnectionPreference.HolePunch, Report(true, false)),
                Planned(SmartLinkConnectionMode.AutomaticHolePunch, false, RadioConnectionPreference.ForwardOnly, Report(false, false)),
            })
            {
                string s = SmartLinkTierOffers.RefusalSentence(rec, "Bench 8600");
                Assert.Contains("Bench 8600", s);
                Assert.DoesNotContain("{", s);
                Assert.DoesNotContain("only be reached by", s);
                Assert.DoesNotContain("DRAFT", s);
            }
        }

        // ---- The Settings warning (the plan's section 2) ----

        [Fact]
        public void Settings_warns_when_the_known_owner_would_refuse_the_choice()
        {
            string? w = SmartLinkTierOffers.SettingsWarning("4711-1111-2222-3333", "owner@example.invalid",
                SmartLinkConnectionMode.ManualPortForwardOnly, false, RadioConnectionPreference.HolePunch, Report(true, false), "Bench 8600");
            Assert.NotNull(w);
            Assert.Contains("Bench 8600", w);
            Assert.Contains("owner@example.invalid", w);
            Assert.DoesNotContain("{", w);
        }

        [Fact]
        public void Settings_says_nothing_when_the_owner_or_report_is_unknown_or_the_choice_is_admitted()
        {
            Assert.Null(SmartLinkTierOffers.SettingsWarning("s", null, SmartLinkConnectionMode.ManualPortForwardOnly, false, RadioConnectionPreference.HolePunch, Report(true, false), "R"));
            Assert.Null(SmartLinkTierOffers.SettingsWarning("s", "o@example.invalid", SmartLinkConnectionMode.ManualPortForwardOnly, false, RadioConnectionPreference.HolePunch, null, "R"));
            Assert.Null(SmartLinkTierOffers.SettingsWarning("s", "o@example.invalid", SmartLinkConnectionMode.AutomaticHolePunch, false, RadioConnectionPreference.HolePunch, Report(true, false), "R"));
        }

        [Fact]
        public void Each_tier_has_a_name()
        {
            var names = new[] { SmartLinkConnectionMode.ManualPortForwardOnly, SmartLinkConnectionMode.ManualPlusUpnp, SmartLinkConnectionMode.AutomaticHolePunch }
                .Select(SmartLinkTierOffers.TierName).ToArray();
            Assert.Equal(3, names.Distinct().Count());
            Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n) || n.StartsWith("connect.", StringComparison.Ordinal)));
        }

        // ---- The chosen flag, in memory only ----

        [Fact]
        public void The_chosen_flag_round_trips_through_the_stored_form()
        {
            var src = new SmartLinkAccount { Email = "a@example.invalid", ConnectionMode = SmartLinkConnectionMode.ManualPlusUpnp, TierChosen = true };
            var json = JsonSerializer.Serialize(SmartLinkAccountManager.StoredAccount.FromSmartLinkAccount(src));
            Assert.Contains("\"tierChosen\":true", json);
            var back = JsonSerializer.Deserialize<SmartLinkAccountManager.StoredAccount>(json)!.ToSmartLinkAccount();
            Assert.True(back.TierChosen);
            Assert.Equal(SmartLinkConnectionMode.ManualPlusUpnp, back.ConnectionMode);
        }

        [Fact]
        public void An_old_file_loads_as_never_chosen()
        {
            const string oldJson = @"{ ""friendlyName"": ""Legacy"", ""email"": ""old@example.invalid"", ""connectionMode"": ""AutomaticHolePunch"" }";
            var back = JsonSerializer.Deserialize<SmartLinkAccountManager.StoredAccount>(oldJson)!.ToSmartLinkAccount();
            Assert.False(back.TierChosen);
            Assert.Equal(SmartLinkConnectionMode.AutomaticHolePunch, back.ConnectionMode);
        }

        [Fact]
        public void An_operator_choice_sets_the_tier_and_marks_it_chosen()
        {
            var a = new SmartLinkAccount { Email = "a@example.invalid" };
            SmartLinkAccountManager.ApplyOperatorChoice(a, SmartLinkConnectionMode.AutomaticHolePunch);
            Assert.True(a.TierChosen);
            Assert.Equal(SmartLinkConnectionMode.AutomaticHolePunch, a.ConnectionMode);
        }

        private static string Src(params string[] parts) =>
            File.ReadAllText(Path.Combine(new[] { IntegrationPassTree.Root }.Concat(parts).ToArray()));

        [Fact]
        public void Only_an_operator_choice_writes_the_chosen_flag()
        {
            // Apply re-saves the tier on every port-forward Apply, so the tier
            // writer it calls must never mark it chosen (settled item 9).
            string flexBase = Src("Radios", "FlexBase.cs");
            int start = flexBase.IndexOf("public bool SaveCurrentAccountConnectionMode(", StringComparison.Ordinal);
            Assert.True(start > 0);
            string body = flexBase.Substring(start, flexBase.IndexOf("\n        }", start, StringComparison.Ordinal) - start);
            Assert.DoesNotContain("TierChosen", body);
            Assert.DoesNotContain("ChooseConnectionMode", body);

            // Across the product, TierChosen is assigned in exactly one place.
            int writers = 0;
            foreach (var dir in new[] { "Radios", "JJFlexWpf" })
                foreach (var f in Directory.GetFiles(Path.Combine(IntegrationPassTree.Root, dir), "*.cs", SearchOption.AllDirectories)
                             .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                             // The accounts dialog keeps a DISPLAY copy (SmartLinkAccountInfo) in step after its
                             // callback saved through ChooseConnectionMode; that copy is not an account.
                             .Where(p => !p.EndsWith("SmartLinkAccountDialog.xaml.cs", StringComparison.OrdinalIgnoreCase)))
                    writers += System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), @"\.TierChosen\s*=[^=]").Count;
            Assert.Equal(1, writers);
        }
    }
}
