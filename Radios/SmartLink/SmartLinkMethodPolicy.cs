#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Flex.Smoothlake.FlexLib;

namespace Radios.SmartLink
{
    /// <summary>
    /// One way of reaching a radio over SmartLink. The order of the values is
    /// not the order of an attempt; <see cref="SmartLinkMethodPolicy"/> decides that.
    /// </summary>
    public enum SmartLinkMethod
    {
        /// <summary>The radio's manually forwarded port pair (Tier 1 and up).</summary>
        ManualPorts = 1,
        /// <summary>The port pair the radio mapped on its own router by UPnP (Tier 2 and up).</summary>
        UpnpPorts = 2,
        /// <summary>A SmartLink-coordinated UDP hole punch (Tier 3).</summary>
        HolePunch = 3,
    }

    /// <summary>Where an advertised port pair came from.</summary>
    public enum PortPairSource
    {
        Manual = 0,
        Upnp = 1,
    }

    /// <summary>
    /// One advertised port pair. Usable only when it is offered and BOTH its
    /// TCP and UDP ports are from 1 to 65535 (Noel's decision 3: both must be
    /// positive to count).
    /// </summary>
    public readonly record struct AdvertisedPortPair(PortPairSource Source, int TlsPort, int UdpPort, bool Offered)
    {
        public bool IsUsable => Offered && InRange(TlsPort) && InRange(UdpPort);
        private static bool InRange(int port) => port >= 1 && port <= 65535;
        public override string ToString() => $"{Source} {TlsPort}/{UdpPort}{(Offered ? "" : " not offered")}";
    }

    /// <summary>
    /// What the radio advertised about its ports: both pairs, each by source,
    /// and the radio's own hole-punch report. Built from FlexLib's
    /// <see cref="AdvertisedPorts"/> (MIGRATION.md item 29) when a SmartLink
    /// list described the radio; otherwise derived from FlexLib's single kept
    /// pair, which is all a radio rebuilt from the connection cache carries.
    /// </summary>
    public sealed record RadioPortReport(
        AdvertisedPortPair Manual,
        AdvertisedPortPair Upnp,
        bool RadioSaysHolePunch,
        bool FromRadioList)
    {
        public static RadioPortReport FromAdvertised(AdvertisedPorts a) => new(
            new AdvertisedPortPair(PortPairSource.Manual, a.ManualTlsPort, a.ManualUdpPort, Offered: true),
            new AdvertisedPortPair(PortPairSource.Upnp, a.UpnpTlsPort, a.UpnpUdpPort, Offered: a.UpnpSupported),
            a.RequiresHolePunch,
            FromRadioList: true);

        /// <summary>
        /// The radio's report. Reads <see cref="Radio.AdvertisedPorts"/> when a
        /// list set it; otherwise FlexLib's kept pair is attributed by its
        /// forwarding flag, and the punch flag is read as it stands.
        /// </summary>
        public static RadioPortReport FromRadio(Radio r)
        {
            if (r.AdvertisedPorts != null) return FromAdvertised(r.AdvertisedPorts);
            bool forwarded = r.IsPortForwardOn;
            bool punch = r.RequiresHolePunch;
            return new RadioPortReport(
                new AdvertisedPortPair(PortPairSource.Manual, forwarded ? r.PublicTlsPort : -1, forwarded ? r.PublicUdpPort : -1, Offered: true),
                new AdvertisedPortPair(PortPairSource.Upnp, !forwarded && !punch ? r.PublicTlsPort : -1, !forwarded && !punch ? r.PublicUdpPort : -1,
                    Offered: !forwarded && !punch),
                punch,
                FromRadioList: false);
        }

        public AdvertisedPortPair PairFor(SmartLinkMethod method) => method switch
        {
            SmartLinkMethod.ManualPorts => Manual,
            SmartLinkMethod.UpnpPorts => Upnp,
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, "a hole punch has no advertised pair"),
        };

        public override string ToString() =>
            $"manual={Manual.TlsPort}/{Manual.UdpPort}{(Manual.IsUsable ? "" : " unusable")} " +
            $"upnp={Upnp.TlsPort}/{Upnp.UdpPort}{(Upnp.IsUsable ? "" : " unusable")} " +
            $"radioSaysPunch={RadioSaysHolePunch} source={(FromRadioList ? "list" : "kept pair")}";
    }

    /// <summary>Why the SmartLink leg was refused before anything was sent.</summary>
    public enum SmartLinkRefusal
    {
        /// <summary>Not refused.</summary>
        None = 0,
        /// <summary>The radio is set to "Hole punch always" and the account's tier does not allow hole punching.</summary>
        HolePunchAlwaysButTierForbidsPunch = 1,
        /// <summary>The account's tier forbids every method the radio offers; a higher tier would admit one (see <see cref="SmartLinkPolicyDecision.LowestAdmittingTier"/>).</summary>
        TierForbidsWhatTheRadioOffers = 2,
        /// <summary>The radio is set to forwarded ports only and advertises no usable port pair the tier allows; no tier would change that.</summary>
        ForwardedOnlyButNoUsablePortPair = 3,
    }

    /// <summary>Everything the policy reads. Nothing here is a radio, a socket or a session.</summary>
    /// <param name="Tier">The ceiling: the tier of the account whose session BROKERS this connect (#798), never the active account by habit.</param>
    /// <param name="TierChosen">Whether an operator chose that tier (the accounts dialog or the offer); only an account never chosen can be offered a change.</param>
    /// <param name="PerRadioChoice">Orders or narrows inside the ceiling; never widens it.</param>
    /// <param name="ForcedPath">"Connect over SmartLink" from the context menu: never Local, but every method allowed. Recorded; it does not change the methods.</param>
    /// <param name="Report">What the radio advertised.</param>
    public sealed record SmartLinkPolicyInput(
        SmartLinkConnectionMode Tier,
        bool TierChosen,
        RadioConnectionPreference PerRadioChoice,
        bool ForcedPath,
        RadioPortReport Report)
    {
        /// <summary>"Hole punch always" pins the METHOD (decision 9); the ceiling still wins over it.</summary>
        public bool ForcedMethod => PerRadioChoice == RadioConnectionPreference.HolePunch;
    }

    /// <summary>The policy's answer: an ordered list of methods, or a refusal and its reason.</summary>
    public sealed record SmartLinkPolicyDecision(
        IReadOnlyList<SmartLinkMethod> Methods,
        SmartLinkRefusal Refusal,
        SmartLinkConnectionMode? LowestAdmittingTier,
        bool PunchWithheldUntilGate)
    {
        public bool IsRefused => Methods.Count == 0;
        public SmartLinkMethod? First => Methods.Count > 0 ? Methods[0] : null;
        public string Describe() => IsRefused
            ? $"refused={Refusal}" + (LowestAdmittingTier is { } t ? $" lowestAdmittingTier={(int)t + 1}" : "")
            : string.Join(",", Methods) + (PunchWithheldUntilGate ? " (punch withheld until the hardware gate passes)" : "");
    }

    /// <summary>
    /// Which SmartLink methods a connect may use, in order. Pure: no radio, no
    /// session, no settings file. Noel's rulings on the tiers plan, 2026-11-19:
    /// <list type="bullet">
    /// <item>The account tier is the ceiling and wins every conflict; the
    /// per-radio choice only orders or narrows (decision 1). SmartLink methods
    /// only: Local, a VPN-reached LAN and Connect are not this policy's.</item>
    /// <item>Tier 1 allows the manual pair; Tier 2 also the radio's own UPnP
    /// pair; Tier 3 also a hole punch (decisions 2 and 7: Tier 1 refuses a
    /// radio that offers only UPnP ports).</item>
    /// <item>A pair counts only when both ports are positive (decision 3).</item>
    /// <item>Automatic: manual pair, then UPnP pair, then a punch. Forwarded
    /// ports only: the pairs only. Hole punch always: the punch only, and no
    /// fallback (decision 9).</item>
    /// <item>Until the hardware gate passes, Tier 3 punches only when the radio
    /// offers no permitted port pair, and the list never has a punch after
    /// ports (decision 4; <see cref="HolePunchFallbackGatePassed"/>).</item>
    /// </list>
    /// </summary>
    public static class SmartLinkMethodPolicy
    {
        /// <summary>
        /// OFF until the hardware gate (the tiers plan's E1, on the 8600) shows
        /// that a punch completes and that a forwarded radio accepts one. While
        /// off, an Automatic Tier 3 list never carries a punch after ports.
        /// Turning it on is a ruling, not a refactor.
        /// </summary>
        public const bool HolePunchFallbackGatePassed = false;

        public static SmartLinkPolicyDecision Decide(SmartLinkPolicyInput input) =>
            Decide(input, HolePunchFallbackGatePassed);

        /// <summary>As <see cref="Decide(SmartLinkPolicyInput)"/>, with the gate given explicitly (tests, and the day it passes).</summary>
        public static SmartLinkPolicyDecision Decide(SmartLinkPolicyInput input, bool gatePassed)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            var methods = MethodsFor(input.Tier, input.PerRadioChoice, input.Report, gatePassed, out bool withheld);
            if (methods.Count > 0)
                return new SmartLinkPolicyDecision(methods, SmartLinkRefusal.None, null, withheld);

            SmartLinkConnectionMode? lowest = null;
            foreach (var t in new[] { SmartLinkConnectionMode.ManualPlusUpnp, SmartLinkConnectionMode.AutomaticHolePunch })
            {
                if ((int)t <= (int)input.Tier) continue;
                if (MethodsFor(t, input.PerRadioChoice, input.Report, gatePassed, out _).Count > 0) { lowest = t; break; }
            }

            SmartLinkRefusal reason =
                input.PerRadioChoice == RadioConnectionPreference.HolePunch ? SmartLinkRefusal.HolePunchAlwaysButTierForbidsPunch
                : lowest != null ? SmartLinkRefusal.TierForbidsWhatTheRadioOffers
                : SmartLinkRefusal.ForwardedOnlyButNoUsablePortPair;
            return new SmartLinkPolicyDecision(Array.Empty<SmartLinkMethod>(), reason, lowest, false);
        }

        /// <summary>What a tier allows, before the radio or the per-radio choice is consulted.</summary>
        public static bool TierAllows(SmartLinkConnectionMode tier, SmartLinkMethod method) => method switch
        {
            SmartLinkMethod.ManualPorts => true,
            SmartLinkMethod.UpnpPorts => tier >= SmartLinkConnectionMode.ManualPlusUpnp,
            SmartLinkMethod.HolePunch => tier >= SmartLinkConnectionMode.AutomaticHolePunch,
            _ => false,
        };

        private static List<SmartLinkMethod> MethodsFor(SmartLinkConnectionMode tier, RadioConnectionPreference choice,
            RadioPortReport report, bool gatePassed, out bool punchWithheld)
        {
            punchWithheld = false;
            var list = new List<SmartLinkMethod>();

            if (choice == RadioConnectionPreference.HolePunch)
            {
                if (TierAllows(tier, SmartLinkMethod.HolePunch)) list.Add(SmartLinkMethod.HolePunch);
                return list;
            }

            if (TierAllows(tier, SmartLinkMethod.ManualPorts) && report.Manual.IsUsable) list.Add(SmartLinkMethod.ManualPorts);
            if (TierAllows(tier, SmartLinkMethod.UpnpPorts) && report.Upnp.IsUsable) list.Add(SmartLinkMethod.UpnpPorts);

            if (choice == RadioConnectionPreference.ForwardOnly) return list;

            // Automatic (and any value a newer build saved that this one does not know).
            if (TierAllows(tier, SmartLinkMethod.HolePunch))
            {
                if (list.Count == 0) list.Add(SmartLinkMethod.HolePunch);
                else if (gatePassed) list.Add(SmartLinkMethod.HolePunch);
                else punchWithheld = true;
            }
            return list;
        }
    }
}
