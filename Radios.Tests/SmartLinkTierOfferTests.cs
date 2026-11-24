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
        public void No_keeps_a_Tier_1_account_at_Tier_1()
        {
            var offer = SmartLinkTierOffers.For(Planned(SmartLinkConnectionMode.ManualPortForwardOnly, false, RadioConnectionPreference.Auto, Report(false, true)), accountIsSaved: true)!;
            Assert.Equal(SmartLinkConnectionMode.ManualPortForwardOnly, SmartLinkTierOffers.TierOnNo(offer));
            Assert.Equal(SmartLinkConnectionMode.ManualPortForwardOnly, SmartLinkTierOffers.TierFor(offer, yes: false));
        }

        /// <summary>
        /// RULED by Noel 2026-11-19 20:34, amending decision 6: No keeps the
        /// account's CURRENT tier, marked chosen, so declining never lowers an
        /// account. A Tier 2 account saved before this build loads as never
        /// chosen; offered Tier 3 and answering No, it stays Tier 2, chosen, and
        /// its next refused connect earns no offer.
        /// </summary>
        [Fact]
        public void A_never_chosen_Tier_2_account_answering_No_stays_Tier_2_chosen_and_is_not_asked_again()
        {
            const string oldJson = @"{ ""friendlyName"": ""Legacy"", ""email"": ""owner@example.invalid"", ""connectionMode"": ""ManualPlusUpnp"" }";
            var account = JsonSerializer.Deserialize<SmartLinkAccountManager.StoredAccount>(oldJson)!.ToSmartLinkAccount();
            Assert.False(account.TierChosen);

            var offer = SmartLinkTierOffers.For(Planned(account.ConnectionMode, account.TierChosen, RadioConnectionPreference.Auto, Report(false, false)), accountIsSaved: true);
            Assert.NotNull(offer);
            Assert.Equal(SmartLinkConnectionMode.AutomaticHolePunch, offer!.OfferedTier);
            Assert.Equal(SmartLinkConnectionMode.ManualPlusUpnp, offer.CurrentTier);

            // What AnswerTierOffer saves on No, applied in memory (no test saves accounts).
            SmartLinkAccountManager.ApplyOperatorChoice(account, SmartLinkTierOffers.TierFor(offer, yes: false));
            Assert.Equal(SmartLinkConnectionMode.ManualPlusUpnp, account.ConnectionMode);
            Assert.True(account.TierChosen);

            // Not asked again: the same radio, and a UPnP-only radio Tier 2 still admits.
            Assert.Null(SmartLinkTierOffers.For(Planned(account.ConnectionMode, account.TierChosen, RadioConnectionPreference.Auto, Report(false, false)), accountIsSaved: true));
            var upnpOnly = SmartLinkMethodPolicy.Decide(new SmartLinkPolicyInput(account.ConnectionMode, account.TierChosen, RadioConnectionPreference.Auto, false, Report(false, true)));
            Assert.Equal(new[] { SmartLinkMethod.UpnpPorts }, upnpOnly.Methods);
        }

        [Fact]
        public void Yes_saves_the_offered_tier_whatever_the_current_one()
        {
            var offer = SmartLinkTierOffers.For(Planned(SmartLinkConnectionMode.ManualPlusUpnp, false, RadioConnectionPreference.Auto, Report(false, false)), accountIsSaved: true)!;
            Assert.Equal(SmartLinkConnectionMode.AutomaticHolePunch, SmartLinkTierOffers.TierFor(offer, yes: true));
        }

        [Fact]
        public void AnswerTierOffer_saves_what_TierFor_says()
        {
            // The one writer of the answer must not decide the tier itself.
            string src = Src("Radios", "FlexBase.ConnectTiers.cs");
            int start = src.IndexOf("public SmartLinkConnectionMode? AnswerTierOffer(", StringComparison.Ordinal);
            Assert.True(start > 0);
            string body = src.Substring(start, src.IndexOf("\n        }", start, StringComparison.Ordinal) - start);
            Assert.Contains("SmartLinkTierOffers.TierFor(offer, yes)", body);
            Assert.DoesNotContain("ManualPortForwardOnly", body);
        }

        [Fact]
        public void The_offer_words_never_say_No_keeps_forwarded_ports_only()
        {
            var upnp = new SmartLinkTierOffer("s", "o@example.invalid", SmartLinkConnectionMode.ManualPlusUpnp,
                SmartLinkConnectionMode.ManualPortForwardOnly, SmartLinkRefusal.TierForbidsWhatTheRadioOffers);
            foreach (var o in new[] { upnp, upnp with { OfferedTier = SmartLinkConnectionMode.AutomaticHolePunch },
                         upnp with { OfferedTier = SmartLinkConnectionMode.AutomaticHolePunch, Refusal = SmartLinkRefusal.HolePunchAlwaysButTierForbidsPunch } })
                Assert.DoesNotContain("forwarded ports only", SmartLinkTierOffers.Question(o, "R"));
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
        public void Only_a_refusal_a_higher_tier_cures_earns_an_offer()
        {
            // Repair round 1 (Codex's B1): this test said "Only_a_tier_refusal_earns_an_offer"
            // and pinned "Hole punch always" under Tier 1 as earning NO offer. That is now
            // an offer (the next two tests); only the two cases below stay offerless.
            // Forwarded ports only with nothing usable: no tier would help.
            Assert.Null(SmartLinkTierOffers.For(Planned(SmartLinkConnectionMode.ManualPortForwardOnly, false, RadioConnectionPreference.ForwardOnly, Report(false, false)), true));
            // Not refused at all.
            Assert.Null(SmartLinkTierOffers.For(Planned(SmartLinkConnectionMode.ManualPortForwardOnly, false, RadioConnectionPreference.Auto, Report(true, false)), true));
        }

        /// <summary>
        /// Codex's B1: a radio advertising no usable pair, set to "Hole punch
        /// always", under a never-chosen Tier 1 or Tier 2 account. It could punch
        /// before this build; the policy now refuses it with
        /// HolePunchAlwaysButTierForbidsPunch, and that refusal earns the offer,
        /// naming Tier 3, the lowest tier that admits the punch the choice demands.
        /// </summary>
        [Fact]
        public void A_punch_only_radio_set_to_Hole_punch_always_is_offered_Tier_3()
        {
            foreach (var tier in new[] { SmartLinkConnectionMode.ManualPortForwardOnly, SmartLinkConnectionMode.ManualPlusUpnp })
            {
                var rec = Planned(tier, false, RadioConnectionPreference.HolePunch, Report(false, false));
                Assert.Equal(SmartLinkRefusal.HolePunchAlwaysButTierForbidsPunch, rec.Refusal);
                var offer = SmartLinkTierOffers.For(rec, accountIsSaved: true);
                Assert.NotNull(offer);
                Assert.Equal(SmartLinkConnectionMode.AutomaticHolePunch, offer!.OfferedTier);
                Assert.Equal(tier, offer.CurrentTier);
                Assert.Equal(SmartLinkRefusal.HolePunchAlwaysButTierForbidsPunch, offer.Refusal);
                Assert.Equal("connect.tier_offer.punch_always", SmartLinkTierOffers.QuestionKey(offer));
                Assert.Equal(tier, SmartLinkTierOffers.TierFor(offer, yes: false));
            }
        }

        /// <summary>
        /// The same refusal on a radio that DOES advertise ports earns the offer
        /// too (the repair brief: both refusals produce it), and keeps its own
        /// words, because the punch is demanded by the radio's choice rather than
        /// needed by the radio; the other refusal keeps the punch words.
        /// </summary>
        [Fact]
        public void Both_tier_refusals_earn_the_offer_with_their_own_words()
        {
            var forced = SmartLinkTierOffers.For(Planned(SmartLinkConnectionMode.ManualPortForwardOnly, false, RadioConnectionPreference.HolePunch, Report(true, false)), true);
            Assert.NotNull(forced);
            Assert.Equal(SmartLinkConnectionMode.AutomaticHolePunch, forced!.OfferedTier);
            Assert.Equal("connect.tier_offer.punch_always", SmartLinkTierOffers.QuestionKey(forced));

            var needed = SmartLinkTierOffers.For(Planned(SmartLinkConnectionMode.ManualPortForwardOnly, false, RadioConnectionPreference.Auto, Report(false, false)), true);
            Assert.NotNull(needed);
            Assert.Equal(SmartLinkRefusal.TierForbidsWhatTheRadioOffers, needed!.Refusal);
            Assert.Equal("connect.tier_offer.punch", SmartLinkTierOffers.QuestionKey(needed));

            Assert.True(SmartLinkTierOffers.EarnsAnOffer(SmartLinkRefusal.HolePunchAlwaysButTierForbidsPunch));
            Assert.True(SmartLinkTierOffers.EarnsAnOffer(SmartLinkRefusal.TierForbidsWhatTheRadioOffers));
            Assert.False(SmartLinkTierOffers.EarnsAnOffer(SmartLinkRefusal.ForwardedOnlyButNoUsablePortPair));
            Assert.False(SmartLinkTierOffers.EarnsAnOffer(SmartLinkRefusal.None));
        }

        [Fact]
        public void A_chosen_Hole_punch_always_account_gets_the_refusal_only()
        {
            Assert.Null(SmartLinkTierOffers.For(Planned(SmartLinkConnectionMode.ManualPlusUpnp, true, RadioConnectionPreference.HolePunch, Report(false, false)), true));
        }

        [Fact]
        public void The_offer_and_the_refusals_have_words_naming_the_radio_and_account_and_the_offer_says_every_radio()
        {
            var upnp = new SmartLinkTierOffer("4711-1111-2222-3333", "owner@example.invalid", SmartLinkConnectionMode.ManualPlusUpnp,
                SmartLinkConnectionMode.ManualPortForwardOnly, SmartLinkRefusal.TierForbidsWhatTheRadioOffers);
            var punch = upnp with { OfferedTier = SmartLinkConnectionMode.AutomaticHolePunch };
            var punchAlways = punch with { Refusal = SmartLinkRefusal.HolePunchAlwaysButTierForbidsPunch };
            foreach (var o in new[] { upnp, punch, punchAlways })
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
