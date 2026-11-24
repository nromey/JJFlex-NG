using System;
using System.Collections.Generic;
using System.Linq;
using Flex.Smoothlake.FlexLib;
using Radios.SmartLink;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The SmartLink method policy, every tier against every per-radio choice
    /// against every advertisement shape, with the hardware gate off and on.
    /// </summary>
    /// <remarks>
    /// The expectations are written out from Noel's rulings on the tiers plan
    /// (2026-11-19), row by row, NOT computed from the code: decision 1 (the
    /// tier is the ceiling, the per-radio choice orders or narrows), 2 and 7
    /// (Tier 2 means the radio's own UPnP ports; Tier 1 refuses a UPnP-only
    /// radio and names Tier 2), 3 (both pairs, each needing two positive
    /// ports), 4 (manual, UPnP, punch; until the gate passes a Tier 3 punch only
    /// when no permitted pair exists) and 9 (Hole punch always pins the method,
    /// the ceiling still wins).
    /// </remarks>
    public sealed class SmartLinkMethodPolicyTests
    {
        private static AdvertisedPortPair M(int tls, int udp) => new(PortPairSource.Manual, tls, udp, true);
        private static AdvertisedPortPair U(int tls, int udp, bool offered = true) => new(PortPairSource.Upnp, tls, udp, offered);

        /// <summary>The advertisement shapes, by name.</summary>
        private static RadioPortReport Shape(string name) => name switch
        {
            "manual" => new(M(4994, 4993), U(-1, -1, false), false, true),
            "upnp" => new(M(-1, -1), U(21000, 21001), false, true),
            "both" => new(M(4994, 4993), U(21000, 21001), false, true),
            "neither" => new(M(-1, -1), U(-1, -1, false), true, true),
            "zero" => new(M(0, 0), U(-1, -1, false), false, true),          // stock's TryParse zero
            "halfmanual" => new(M(4994, -1), U(-1, -1, false), false, true), // TCP without UDP
            "halfupnp" => new(M(-1, -1), U(21000, 0), false, true),          // UPnP TCP without UDP
            "upnpoff" => new(M(-1, -1), U(21000, 21001, false), true, true), // UPnP ports listed, upnp_supported=0
            _ => throw new ArgumentException(name),
        };

        private static SmartLinkConnectionMode Tier(int n) => (SmartLinkConnectionMode)(n - 1);

        private static RadioConnectionPreference Choice(string c) => c switch
        {
            "auto" => RadioConnectionPreference.Auto,
            "forward" => RadioConnectionPreference.ForwardOnly,
            "punch" => RadioConnectionPreference.HolePunch,
            _ => throw new ArgumentException(c),
        };

        /// <summary>
        /// Expected: a comma list of methods (m = manual pair, u = UPnP pair, p = punch),
        /// or "refuse:" + reason + ":" + lowest admitting tier (0 for none).
        /// One row per tier, choice and shape; the gate column says which gate states the row is for.
        /// </summary>
        public static IEnumerable<object[]> Rulings()
        {
            string[] noPairs = { "neither", "zero", "halfmanual", "upnpoff" };
            // ---- Automatic ----
            yield return R(1, "auto", "manual", "m");
            yield return R(1, "auto", "upnp", "refuse:tier:2");      // decision 7
            yield return R(1, "auto", "both", "m");
            yield return R(1, "auto", "halfupnp", "refuse:tier:3");
            foreach (var s in noPairs) yield return R(1, "auto", s, "refuse:tier:3");

            yield return R(2, "auto", "manual", "m");
            yield return R(2, "auto", "upnp", "u");
            yield return R(2, "auto", "both", "m,u");
            yield return R(2, "auto", "halfupnp", "refuse:tier:3");
            foreach (var s in noPairs) yield return R(2, "auto", s, "refuse:tier:3");

            // Tier 3 before the gate: ports only when ports exist; a punch only when none do.
            yield return R(3, "auto", "manual", "m", gate: false);
            yield return R(3, "auto", "upnp", "u", gate: false);
            yield return R(3, "auto", "both", "m,u", gate: false);
            // ...and after it: ports, then the punch.
            yield return R(3, "auto", "manual", "m,p", gate: true);
            yield return R(3, "auto", "upnp", "u,p", gate: true);
            yield return R(3, "auto", "both", "m,u,p", gate: true);
            foreach (bool g in new[] { false, true })
            {
                yield return R(3, "auto", "halfupnp", "p", gate: g);
                foreach (var s in noPairs) yield return R(3, "auto", s, "p", gate: g);
            }

            // ---- Forwarded ports only: the pairs only, never a punch ----
            yield return R(1, "forward", "manual", "m");
            yield return R(1, "forward", "upnp", "refuse:tier:2");
            yield return R(1, "forward", "both", "m");
            yield return R(1, "forward", "halfupnp", "refuse:forward:0");
            foreach (var s in noPairs) yield return R(1, "forward", s, "refuse:forward:0");
            foreach (int t in new[] { 2, 3 })
            {
                yield return R(t, "forward", "manual", "m");
                yield return R(t, "forward", "upnp", "u");
                yield return R(t, "forward", "both", "m,u");
                yield return R(t, "forward", "halfupnp", "refuse:forward:0");
                foreach (var s in noPairs) yield return R(t, "forward", s, "refuse:forward:0");
            }

            // ---- Hole punch always: the punch only; the ceiling wins ----
            foreach (var s in new[] { "manual", "upnp", "both", "halfupnp" }.Concat(noPairs))
            {
                yield return R(1, "punch", s, "refuse:punch:3");
                yield return R(2, "punch", s, "refuse:punch:3");
                yield return R(3, "punch", s, "p");
            }
        }

        private static object[] R(int tier, string choice, string shape, string expected, bool? gate = null) =>
            new object[] { tier, choice, shape, gate.HasValue ? (gate.Value ? "on" : "off") : "both", expected };

        [Theory]
        [MemberData(nameof(Rulings))]
        public void The_policy_follows_the_rulings(int tier, string choice, string shape, string gate, string expected)
        {
            var gates = gate switch { "on" => new[] { true }, "off" => new[] { false }, _ => new[] { false, true } };
            foreach (bool gatePassed in gates)
                foreach (bool chosen in new[] { false, true })
                    foreach (bool forcedPath in new[] { false, true })
                    {
                        var input = new SmartLinkPolicyInput(Tier(tier), chosen, Choice(choice), forcedPath, Shape(shape));
                        var d = SmartLinkMethodPolicy.Decide(input, gatePassed);
                        string why = $"tier {tier}, {choice}, {shape}, gate {(gatePassed ? "on" : "off")}, chosen {chosen}, forcedPath {forcedPath}: {d.Describe()}";
                        if (expected.StartsWith("refuse:"))
                        {
                            var parts = expected.Split(':');
                            Assert.True(d.IsRefused, why);
                            Assert.Empty(d.Methods);
                            var reason = parts[1] switch
                            {
                                "tier" => SmartLinkRefusal.TierForbidsWhatTheRadioOffers,
                                "forward" => SmartLinkRefusal.ForwardedOnlyButNoUsablePortPair,
                                "punch" => SmartLinkRefusal.HolePunchAlwaysButTierForbidsPunch,
                                _ => throw new ArgumentException(parts[1]),
                            };
                            Assert.True(reason == d.Refusal, why);
                            int lowest = int.Parse(parts[2]);
                            Assert.True((lowest == 0 ? (SmartLinkConnectionMode?)null : Tier(lowest)) == d.LowestAdmittingTier, why);
                        }
                        else
                        {
                            var want = expected.Split(',').Select(c => c switch
                            {
                                "m" => SmartLinkMethod.ManualPorts,
                                "u" => SmartLinkMethod.UpnpPorts,
                                "p" => SmartLinkMethod.HolePunch,
                                _ => throw new ArgumentException(c),
                            }).ToArray();
                            Assert.True(want.SequenceEqual(d.Methods), why);
                            Assert.Equal(SmartLinkRefusal.None, d.Refusal);
                        }
                    }
        }

        [Fact]
        public void Every_tier_choice_and_shape_has_a_ruling_row()
        {
            var rows = Rulings().Select(r => ((int)r[0], (string)r[1], (string)r[2])).ToHashSet();
            foreach (int t in new[] { 1, 2, 3 })
                foreach (var c in new[] { "auto", "forward", "punch" })
                    foreach (var s in new[] { "manual", "upnp", "both", "neither", "zero", "halfmanual", "halfupnp", "upnpoff" })
                        Assert.Contains((t, c, s), rows);
        }

        [Fact]
        public void Before_the_gate_a_Tier_3_list_never_has_a_punch_after_ports_and_says_it_withheld_one()
        {
            var d = SmartLinkMethodPolicy.Decide(
                new SmartLinkPolicyInput(SmartLinkConnectionMode.AutomaticHolePunch, true, RadioConnectionPreference.Auto, false, Shape("manual")));
            Assert.Equal(new[] { SmartLinkMethod.ManualPorts }, d.Methods);
            Assert.True(d.PunchWithheldUntilGate);
            Assert.Contains("withheld", d.Describe());
        }

        [Fact]
        public void The_gate_switch_is_off()
        {
            Assert.False(SmartLinkMethodPolicy.HolePunchFallbackGatePassed);
        }

        [Fact]
        public void Hole_punch_always_is_the_forced_method_and_a_forced_path_is_not()
        {
            Assert.True(new SmartLinkPolicyInput(SmartLinkConnectionMode.ManualPortForwardOnly, false, RadioConnectionPreference.HolePunch, false, Shape("manual")).ForcedMethod);
            Assert.False(new SmartLinkPolicyInput(SmartLinkConnectionMode.ManualPortForwardOnly, false, RadioConnectionPreference.Auto, true, Shape("manual")).ForcedMethod);
        }

        // ---- The report, from a radio no list described ----

        private static Radio OfflineRadio(bool forwarded, bool punch, int tls, int udp)
        {
            var r = Radio.CreateFromIpWan("FLEX-8600", "1111-2222-3333-4444", "Bench", System.Net.IPAddress.Parse("192.0.2.1"),
                "4.2.20.41343", tls, punch, forwarded);
            r.PublicUdpPort = udp;
            return r;
        }

        [Fact]
        public void A_cached_radio_s_kept_pair_is_attributed_by_its_forwarding_flag()
        {
            var fwd = RadioPortReport.FromRadio(OfflineRadio(forwarded: true, punch: false, 4994, 4993));
            Assert.False(fwd.FromRadioList);
            Assert.True(fwd.Manual.IsUsable);
            Assert.False(fwd.Upnp.IsUsable);

            var upnp = RadioPortReport.FromRadio(OfflineRadio(forwarded: false, punch: false, 21000, 21001));
            Assert.False(upnp.Manual.IsUsable);
            Assert.True(upnp.Upnp.IsUsable);

            var punch = RadioPortReport.FromRadio(OfflineRadio(forwarded: false, punch: true, -1, -1));
            Assert.False(punch.Manual.IsUsable);
            Assert.False(punch.Upnp.IsUsable);
            Assert.True(punch.RadioSaysHolePunch);
        }

        [Fact]
        public void A_listed_radio_s_report_comes_from_both_pairs_not_the_kept_one()
        {
            var r = OfflineRadio(forwarded: true, punch: false, 4994, 4993);
            r.AdvertisedPorts = new AdvertisedPorts(4994, 4993, true, 21000, 21001, false);
            var report = RadioPortReport.FromRadio(r);
            Assert.True(report.FromRadioList);
            Assert.True(report.Manual.IsUsable);
            Assert.True(report.Upnp.IsUsable);
            Assert.Equal(21000, report.PairFor(SmartLinkMethod.UpnpPorts).TlsPort);
        }

        [Theory]
        [InlineData(1, 1, true)]
        [InlineData(65535, 65535, true)]
        [InlineData(0, 4993, false)]
        [InlineData(4994, 0, false)]
        [InlineData(-1, 4993, false)]
        [InlineData(65536, 4993, false)]
        public void A_pair_is_usable_only_with_both_ports_in_range(int tls, int udp, bool usable)
        {
            Assert.Equal(usable, M(tls, udp).IsUsable);
            Assert.False(U(tls, udp, offered: false).IsUsable);
        }
    }
}
