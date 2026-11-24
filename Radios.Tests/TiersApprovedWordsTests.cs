using System;
using System.IO;
using Radios.SmartLink;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The SmartLink connection tiers' words as Noel approved them on
    /// 2026-11-20 (Codex's wording), pinned as the operator hears them: every
    /// placeholder filled, every wrapper around them. A test that asserts only
    /// that the radio's name appears passes for any wording; these assert the
    /// whole sentence, so a later edit has to be a deliberate one.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class TiersApprovedWordsTests
    {
        private const string Radio = "Bench 8600";
        private const string Account = "owner@example.invalid";

        // The lexicon call as source text, split so the key coverage sweep,
        // which reads a quoted key after that call even inside a test's string,
        // does not take these source checks for calls.
        private const string GetCall = "Lexicon" + ".Get(";

        private static RadioPortReport Report(bool manual, bool upnp) => new(
            new AdvertisedPortPair(PortPairSource.Manual, manual ? 4994 : -1, manual ? 4993 : -1, true),
            new AdvertisedPortPair(PortPairSource.Upnp, upnp ? 21000 : -1, upnp ? 21001 : -1, upnp), !manual && !upnp, true);

        private static SmartLinkConnectRecord Planned(SmartLinkConnectionMode tier, RadioConnectionPreference choice, RadioPortReport report)
        {
            var input = new SmartLinkPolicyInput(tier, false, choice, false, report);
            return SmartLinkConnectRecord.Begin("4711-1111-2222-3333", Account, input, SmartLinkMethodPolicy.Decide(input), DateTime.UtcNow);
        }

        // ---- The four refusals, as filed and spoken ----

        [Fact]
        public void A_UPnP_only_radio_under_Tier_1_is_refused_in_the_approved_words()
        {
            Assert.Equal(
                "SmartLink cannot try the ports advertised for Bench 8600. The radio advertises only ports opened by its router through Universal Plug and Play (UPnP), but the account owner@example.invalid allows manually forwarded ports only. Change the account's tier in Manage SmartLink Accounts, on the Radio menu, or configure manual port forwarding at the radio's network location.",
                SmartLinkTierOffers.RefusalSentence(
                    Planned(SmartLinkConnectionMode.ManualPortForwardOnly, RadioConnectionPreference.Auto, Report(false, true)), Radio));
        }

        [Fact]
        public void A_radio_with_no_usable_ports_under_a_tier_without_the_punch_is_refused_in_the_approved_words()
        {
            Assert.Equal(
                "SmartLink cannot try a connection to Bench 8600 using the current tier. The radio advertises no usable ports, and the account owner@example.invalid does not allow hole punching. Change the account's tier in Manage SmartLink Accounts, on the Radio menu, or configure port forwarding at the radio's network location.",
                SmartLinkTierOffers.RefusalSentence(
                    Planned(SmartLinkConnectionMode.ManualPortForwardOnly, RadioConnectionPreference.Auto, Report(false, false)), Radio));
        }

        [Fact]
        public void Hole_punch_always_under_a_tier_without_the_punch_is_refused_in_the_approved_words()
        {
            Assert.Equal(
                "SmartLink cannot try hole punching for Bench 8600. This radio is set to Hole punch always, but the account owner@example.invalid does not allow that method. Change the radio's connection choice in Settings, under Per-radio settings, or change the account's tier in Manage SmartLink Accounts, on the Radio menu.",
                SmartLinkTierOffers.RefusalSentence(
                    Planned(SmartLinkConnectionMode.ManualPortForwardOnly, RadioConnectionPreference.HolePunch, Report(true, false)), Radio));
        }

        [Fact]
        public void Forwarded_ports_only_with_no_usable_ports_is_refused_in_the_approved_words()
        {
            Assert.Equal(
                "SmartLink cannot try a connection to Bench 8600. This radio is set to Forwarded ports only, but advertises no usable ports for that method. Configure port forwarding at the radio's network location, or change the radio's connection choice in Settings, under Per-radio settings.",
                SmartLinkTierOffers.RefusalSentence(
                    Planned(SmartLinkConnectionMode.AutomaticHolePunch, RadioConnectionPreference.ForwardOnly, Report(false, false)), Radio));
        }

        [Fact]
        public void Settings_warns_about_a_refused_choice_in_the_approved_words_with_the_refusal_after_it()
        {
            Assert.Equal(
                "This connection choice would prevent a SmartLink connection to Bench 8600 under the current settings. SmartLink cannot try hole punching for Bench 8600. This radio is set to Hole punch always, but the account owner@example.invalid does not allow that method. Change the radio's connection choice in Settings, under Per-radio settings, or change the account's tier in Manage SmartLink Accounts, on the Radio menu.",
                SmartLinkTierOffers.SettingsWarning("4711-1111-2222-3333", Account,
                    SmartLinkConnectionMode.ManualPortForwardOnly, false, RadioConnectionPreference.HolePunch, Report(true, false), Radio));
        }

        // ---- The reasons a SmartLink attempt gives, inside the retry verdict ----

        private static string RetryVerdict(string reasonKey)
        {
            // globals.vb appends the full stop to the open's reason before it
            // fills {retryReason}; the reasons themselves carry none.
            string reason = Lexicon.Get(reasonKey);
            Assert.False(reason.EndsWith(".", StringComparison.Ordinal), reasonKey + ": the driver adds the full stop");
            return Lexicon.Get("connect.walk.retry_failed_with_reason", ("retryReason", reason + "."));
        }

        [Fact]
        public void A_lost_or_failed_connection_before_the_proof_reads_as_the_approved_reason()
        {
            Assert.Equal(
                "Connection failed. The connection could not be confirmed during radio setup. Please try Remote again.",
                RetryVerdict("connect.start.connection_unconfirmed"));
        }

        [Fact]
        public void A_cancelled_attempt_reads_as_the_approved_reason()
        {
            Assert.Equal(
                "Connection failed. The connection attempt was cancelled. Please try Remote again.",
                RetryVerdict("connect.start.attempt_cancelled"));
        }

        [Fact]
        public void The_SmartLink_attempt_reads_its_reasons_from_the_lexicon_and_no_longer_writes_them()
        {
            string source = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "Radios", "FlexBase.SmartLinkAttempts.cs"));
            Assert.Contains(GetCall + "\"connect.start.connection_unconfirmed\")", source);
            Assert.Contains(GetCall + "\"connect.start.attempt_cancelled\")", source);
            Assert.DoesNotContain("\"Connection lost during setup\"", source);
            Assert.DoesNotContain("\"Cancelled by user\"", source);
        }

        // ---- The failed hole punch's report reads the overlay's sentence from its one home ----

        [Fact]
        public void The_failed_punch_report_appends_the_overlay_sentence_and_drops_the_retry_advice()
        {
            string source = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "Radios", "FlexBase.SmartLink.cs"));
            Assert.Contains("spoken += \" \" + " + GetCall + "\"connect.session_overlay.radio_site_no_punch\");", source);
            Assert.DoesNotContain("Trying again sometimes wins the timing", source);
            Assert.Equal(
                "SmartLink's last test reported no hole-punch support at the radio's network location.",
                Lexicon.Get("connect.session_overlay.radio_site_no_punch"));
        }
    }
}
