#nullable enable

using System;

namespace Radios.SmartLink
{
    /// <summary>
    /// The one-time offer to raise an account's SmartLink tier (the tiers plan's
    /// decisions 6 and 7, ruled by Noel 2026-11-19).
    /// </summary>
    /// <param name="Serial">The radio the refused connect was for.</param>
    /// <param name="AccountEmail">The account whose tier refused it (the brokering account).</param>
    /// <param name="OfferedTier">The LOWEST tier that admits what the radio needs: Tier 2 for a UPnP-only radio, Tier 3 for one that can be reached only by a punch or is set to "Hole punch always".</param>
    /// <param name="CurrentTier">The account's tier when it refused: what a No keeps (Noel, 2026-11-19 20:34).</param>
    /// <param name="Refusal">The refusal that earned the offer; it picks the question's words.</param>
    public sealed record SmartLinkTierOffer(string Serial, string AccountEmail, SmartLinkConnectionMode OfferedTier,
        SmartLinkConnectionMode CurrentTier, SmartLinkRefusal Refusal);

    /// <summary>
    /// When the offer is made, what each answer saves, and the refusal sentences.
    /// Pure, apart from reading the lexicon for words.
    /// <list type="bullet">
    /// <item>Only when the connect failed because the tier forbids a method the
    /// radio needs: what it offers (<see cref="SmartLinkRefusal.TierForbidsWhatTheRadioOffers"/>),
    /// or the punch its "Hole punch always" choice demands
    /// (<see cref="SmartLinkRefusal.HolePunchAlwaysButTierForbidsPunch"/>). Never
    /// on any other failure, and never for "Forwarded ports only" with no usable
    /// pair, which no tier would change.</item>
    /// <item>Only to an account whose tier was never chosen, and only one that is
    /// saved on this computer (an unsaved account has nowhere to keep an answer).</item>
    /// <item>Yes saves the offered tier as chosen and re-plans; No saves the
    /// account's CURRENT tier as chosen, so declining never lowers an account
    /// (RULED by Noel 2026-11-19 20:34, amending decision 6, which said Tier 1),
    /// and the account is never asked again. A Yes covers every radio on that
    /// account, and the words say so.</item>
    /// <item>A chosen account gets the refusal sentence only.</item>
    /// </list>
    /// </summary>
    public static class SmartLinkTierOffers
    {
        /// <summary>The offer this refused connect earns, or null when it earns none.</summary>
        public static SmartLinkTierOffer? For(SmartLinkConnectRecord record, bool accountIsSaved)
        {
            if (record == null) return null;
            if (!EarnsAnOffer(record.Refusal)) return null;
            if (record.TierChosen || !accountIsSaved) return null;
            if (record.LowestAdmittingTier is not SmartLinkConnectionMode tier) return null;
            if ((int)tier <= (int)record.Tier) return null;
            if (string.IsNullOrEmpty(record.BrokeringAccount)) return null;
            return new SmartLinkTierOffer(record.Serial, record.BrokeringAccount, tier, record.Tier, record.Refusal);
        }

        /// <summary>
        /// The refusals a higher tier would cure: the radio needs a method the
        /// tier forbids. "Forwarded ports only" with no usable pair is not one:
        /// no tier gives a radio a port it does not advertise.
        /// </summary>
        public static bool EarnsAnOffer(SmartLinkRefusal refusal) =>
            refusal == SmartLinkRefusal.TierForbidsWhatTheRadioOffers
            || refusal == SmartLinkRefusal.HolePunchAlwaysButTierForbidsPunch;

        /// <summary>The tier a Yes saves, as chosen.</summary>
        public static SmartLinkConnectionMode TierOnYes(SmartLinkTierOffer offer) => offer.OfferedTier;

        /// <summary>
        /// The tier a No saves, as chosen: the account's CURRENT tier, so a No
        /// never lowers an account (RULED by Noel 2026-11-19 20:34, amending
        /// decision 6). A Tier 1 account stays Tier 1; a Tier 2 account
        /// offered Tier 3 stays Tier 2.
        /// </summary>
        public static SmartLinkConnectionMode TierOnNo(SmartLinkTierOffer offer) => offer.CurrentTier;

        /// <summary>What an answer saves, as chosen.</summary>
        public static SmartLinkConnectionMode TierFor(SmartLinkTierOffer offer, bool yes) =>
            yes ? TierOnYes(offer) : TierOnNo(offer);

        /// <summary>A tier's name, as the accounts dialog and the Network tab say it. DRAFT words (connect.json).</summary>
        public static string TierName(SmartLinkConnectionMode tier) => Lexicon.Get(tier switch
        {
            SmartLinkConnectionMode.ManualPlusUpnp => "connect.smartlink.account.tier_2",
            SmartLinkConnectionMode.AutomaticHolePunch => "connect.smartlink.account.tier_3",
            _ => "connect.smartlink.account.tier_1",
        });

        /// <summary>
        /// The Settings warning (the tiers plan, section 2): when a per-radio
        /// connection choice is saved that the radio's known owning account
        /// would refuse, say so then. Null when the owner or the radio's report
        /// is not known (Settings can only warn when the owner is known), or
        /// when the choice would not be refused. DRAFT words.
        /// </summary>
        public static string? SettingsWarning(string serial, string? owningAccount, SmartLinkConnectionMode tier, bool tierChosen,
            RadioConnectionPreference choice, RadioPortReport? report, string radioName)
        {
            if (string.IsNullOrEmpty(owningAccount) || report == null) return null;
            var input = new SmartLinkPolicyInput(tier, tierChosen, choice, false, report);
            var decision = SmartLinkMethodPolicy.Decide(input);
            if (!decision.IsRefused) return null;
            var record = SmartLinkConnectRecord.Begin(serial, owningAccount, input, decision, DateTime.UtcNow);
            return Lexicon.Get("settings.profile.choice_would_be_refused",
                ("disp", radioName), ("reason", RefusalSentence(record, radioName)));
        }

        /// <summary>The offer's title. DRAFT words (connect.json).</summary>
        public static string Title() => Lexicon.Get("connect.tier_offer.title");

        /// <summary>The offer's question. DRAFT words (connect.json).</summary>
        public static string Question(SmartLinkTierOffer offer, string radioName) =>
            Lexicon.Get(QuestionKey(offer), ("radioName", radioName), ("email", offer.AccountEmail));

        /// <summary>
        /// The question's lexicon key. A "Hole punch always" radio gets its own
        /// words: it is refused because its choice demands a punch, which is not
        /// the same as a radio that can be reached in no other way, and the
        /// operator may rather change the radio's choice than the account.
        /// </summary>
        public static string QuestionKey(SmartLinkTierOffer offer) =>
            offer.Refusal == SmartLinkRefusal.HolePunchAlwaysButTierForbidsPunch ? "connect.tier_offer.punch_always"
            : offer.OfferedTier == SmartLinkConnectionMode.ManualPlusUpnp ? "connect.tier_offer.upnp"
            : "connect.tier_offer.punch";

        /// <summary>
        /// The sentence a refused SmartLink leg files as its
        /// <c>PreflightRefused</c> report. It names the radio, the account and
        /// where each setting lives, and never claims a punch is the only way
        /// to the radio: Local, a VPN or Connect may still exist. DRAFT words.
        /// </summary>
        public static string RefusalSentence(SmartLinkConnectRecord record, string radioName)
        {
            var args = new (string, object)[] { ("radioName", radioName), ("email", record.BrokeringAccount) };
            string key = record.Refusal switch
            {
                SmartLinkRefusal.HolePunchAlwaysButTierForbidsPunch => "connect.failure.smartlink_tier_punch_always",
                SmartLinkRefusal.TierForbidsWhatTheRadioOffers =>
                    record.LowestAdmittingTier == SmartLinkConnectionMode.ManualPlusUpnp
                        ? "connect.failure.smartlink_tier_upnp_only"
                        : "connect.failure.smartlink_tier_no_ports",
                SmartLinkRefusal.ForwardedOnlyButNoUsablePortPair => "connect.failure.smartlink_forward_only_no_ports",
                _ => throw new ArgumentException("not a refusal: " + record.Refusal, nameof(record)),
            };
            return Lexicon.Get(key, args);
        }
    }
}
