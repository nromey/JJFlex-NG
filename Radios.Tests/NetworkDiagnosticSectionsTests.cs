using System;
using System.Collections.Generic;
using System.Linq;
using Radios.SmartLink;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The network diagnostic's three separate parts (the tiers plan, section
    /// 5; SmartLink connection tiers, Track C): capability, policy (now, and as
    /// recorded at the last connect) and outcome, composed from fakes. The
    /// expectations are written from the plan's rules, not read off the code.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class NetworkDiagnosticSectionsTests
    {
        private const string Account = "owner@example.invalid";
        private const string RadioName = "Test Radio";
        private static readonly DateTime When = new(2026, 11, 19, 21, 0, 0, DateTimeKind.Utc);

        private static readonly AdvertisedPortPair ManualOk = new(PortPairSource.Manual, 4994, 4993, true);
        private static readonly AdvertisedPortPair ManualNone = new(PortPairSource.Manual, -1, -1, true);
        private static readonly AdvertisedPortPair UpnpOk = new(PortPairSource.Upnp, 21000, 21001, true);
        private static readonly AdvertisedPortPair UpnpNone = new(PortPairSource.Upnp, -1, -1, false);

        private static RadioPortReport Ports(AdvertisedPortPair manual, AdvertisedPortPair upnp, bool punch = false) =>
            new(manual, upnp, punch, true);

        private static readonly DiagnosticPairing Paired = new(DiagnosticPairingKind.Paired, Account, "test");

        private static NetworkDiagnosticReport Probe(bool? fTcp = null, bool? fUdp = null, bool? uTcp = null,
            bool? uUdp = null, bool? nat = null, string error = null, bool timedOut = false) => new()
        {
            RadioSerial = "S-1",
            TimestampUtc = When,
            ManualForwardTcpReachable = fTcp,
            ManualForwardUdpReachable = fUdp,
            UpnpTcpReachable = uTcp,
            UpnpUdpReachable = uUdp,
            NatSupportsHolePunch = nat,
            ErrorDetail = error,
            TimedOut = timedOut,
        };

        private static NetworkDiagnosticInputs Inputs(
            DiagnosticPairing pairing = null,
            NetworkDiagnosticReport probe = null,
            bool? forwardingOn = true,
            SmartLinkConnectionMode tier = SmartLinkConnectionMode.ManualPortForwardOnly,
            bool chosen = true,
            RadioConnectionPreference choice = RadioConnectionPreference.Auto,
            RadioPortReport ports = null,
            bool portsFromLastConnect = false,
            SmartLinkConnectRecord last = null) =>
            new("S-1", RadioName, pairing ?? Paired, probe, forwardingOn, tier, chosen, choice,
                ports, portsFromLastConnect, last);

        private static SmartLinkConnectRecord Record(SmartLinkConnectionMode tier, bool chosen,
            RadioConnectionPreference choice, RadioPortReport ports, bool forcedPath = false, string serial = "S-1")
        {
            var input = new SmartLinkPolicyInput(tier, chosen, choice, forcedPath, ports);
            return SmartLinkConnectRecord.Begin(serial, Account, input, SmartLinkMethodPolicy.Decide(input), When);
        }

        private const string DiagPrefix = "settings.network.diag.";

        private static string D(string key, params (string, object)[] args) =>
            Lexicon.Get(DiagPrefix + key, args);

        // ── The summary rule ────────────────────────────────────────────

        /// <summary>
        /// Every combination of known results, against the rule as the plan
        /// states it: judge the forwarded pair when forwarding is on, else the
        /// UPnP pair; both pass is reachable; otherwise a punch is "possible"
        /// only when the router supports one and forwarding is off; otherwise
        /// not reachable.
        /// </summary>
        [Fact]
        public void The_summary_follows_the_one_rule_for_every_known_combination()
        {
            var bools = new[] { false, true };
            int checkedCount = 0;
            foreach (bool fwd in bools)
            foreach (bool fTcp in bools)
            foreach (bool fUdp in bools)
            foreach (bool uTcp in bools)
            foreach (bool uUdp in bools)
            foreach (bool nat in bools)
            {
                bool judgedTcp = fwd ? fTcp : uTcp;
                bool judgedUdp = fwd ? fUdp : uUdp;
                CapabilitySummary expected =
                    judgedTcp && judgedUdp ? CapabilitySummary.ReachableByPorts
                    : nat && !fwd ? CapabilitySummary.PossibleByHolePunch
                    : CapabilitySummary.NotReachable;
                var actual = NetworkDiagnosticComposer.Summarize(Probe(fTcp, fUdp, uTcp, uUdp, nat), fwd);
                Assert.True(expected == actual,
                    $"fwd={fwd} fwdTcp={fTcp} fwdUdp={fUdp} upnpTcp={uTcp} upnpUdp={uUdp} nat={nat}: expected {expected}, got {actual}");
                checkedCount++;
            }
            Assert.Equal(64, checkedCount);
        }

        [Fact]
        public void Unknown_stays_unknown()
        {
            // No test, an incomplete test, a timed-out test, or no list to say
            // whether forwarding is on: nothing is guessed.
            Assert.Equal(CapabilitySummary.Unknown, NetworkDiagnosticComposer.Summarize(null, true));
            Assert.Equal(CapabilitySummary.Unknown, NetworkDiagnosticComposer.Summarize(Probe(error: "Timed out after 30 seconds", timedOut: true), true));
            Assert.Equal(CapabilitySummary.Unknown, NetworkDiagnosticComposer.Summarize(Probe(error: "lost"), false));
            Assert.Equal(CapabilitySummary.Unknown, NetworkDiagnosticComposer.Summarize(Probe(true, true, true, true, true), null));
            // A judged port unanswered and none failed: the ports may yet be fine.
            Assert.Equal(CapabilitySummary.Unknown, NetworkDiagnosticComposer.Summarize(Probe(fTcp: true, fUdp: null), true));
            Assert.Equal(CapabilitySummary.Unknown, NetworkDiagnosticComposer.Summarize(Probe(uTcp: null, uUdp: null, nat: true), false));
            // A judged port failed, forwarding off, and the router's punch answer unknown.
            Assert.Equal(CapabilitySummary.Unknown, NetworkDiagnosticComposer.Summarize(Probe(uTcp: false, uUdp: true, nat: null), false));
            // A judged port failed with forwarding on: a punch is not on offer whatever nat says.
            Assert.Equal(CapabilitySummary.NotReachable, NetworkDiagnosticComposer.Summarize(Probe(fTcp: false, fUdp: null, nat: true), true));
            // The other pair's results never decide it.
            Assert.Equal(CapabilitySummary.ReachableByPorts, NetworkDiagnosticComposer.Summarize(Probe(true, true, null, null, null), true));
            Assert.Equal(CapabilitySummary.ReachableByPorts, NetworkDiagnosticComposer.Summarize(Probe(null, null, true, true, null), false));
        }

        // ── Capability ──────────────────────────────────────────────────

        [Theory]
        [InlineData(true, "state_reachable")]
        [InlineData(false, "state_not_reachable")]
        [InlineData(null, "state_unknown")]
        public void Each_port_result_is_said_as_it_was_measured(bool? value, string stateKey)
        {
            var s = NetworkDiagnosticComposer.Compose(Inputs(probe: Probe(value, value, value, value, value)));
            string state = D(stateKey);
            Assert.Contains(D("capability_forwarded", ("tcp", state), ("udp", state)), s.Capability.Lines);
            Assert.Contains(D("capability_upnp", ("tcp", state), ("udp", state)), s.Capability.Lines);
            string yn = value switch { true => D("state_yes"), false => D("state_no"), _ => D("state_unknown") };
            Assert.Contains(D("capability_punch", ("state", yn)), s.Capability.Lines);
        }

        [Fact]
        public void Each_of_the_five_results_is_read_from_its_own_flag()
        {
            // One flag true at a time: the line naming it says reachable, and no other does.
            var probe = Probe(fTcp: true, fUdp: false, uTcp: null, uUdp: false, nat: false);
            var lines = NetworkDiagnosticComposer.Compose(Inputs(probe: probe)).Capability.Lines;
            Assert.Contains(D("capability_forwarded", ("tcp", D("state_reachable")), ("udp", D("state_not_reachable"))), lines);
            Assert.Contains(D("capability_upnp", ("tcp", D("state_unknown")), ("udp", D("state_not_reachable"))), lines);
            Assert.Contains(D("capability_punch", ("state", D("state_no"))), lines);
        }

        [Fact]
        public void A_timed_out_test_and_a_test_that_failed_otherwise_are_said_differently()
        {
            var timedOut = NetworkDiagnosticComposer.Compose(Inputs(probe: Probe(error: "Timed out after 30 seconds", timedOut: true)));
            var failed = NetworkDiagnosticComposer.Compose(Inputs(probe: Probe(error: "SendTestConnection threw: x")));
            string time = NetworkDiagnosticComposer.FormatTime(When);
            Assert.Contains(D("capability_timed_out", ("time", time)), timedOut.Capability.Lines);
            Assert.Contains(D("capability_did_not_complete", ("time", time), ("detail", "SendTestConnection threw: x")), failed.Capability.Lines);
            Assert.Null(timedOut.SummaryLine);
            Assert.Null(failed.SummaryLine);
            Assert.Equal(CapabilitySummary.Unknown, timedOut.Summary);
        }

        [Fact]
        public void With_no_test_the_capability_part_says_why_for_each_pairing()
        {
            var noAccount = NetworkDiagnosticComposer.Compose(Inputs(pairing: DiagnosticPairing.None));
            var notSignedIn = NetworkDiagnosticComposer.Compose(Inputs(
                pairing: new DiagnosticPairing(DiagnosticPairingKind.AccountNotSignedIn, Account, "test")));
            var notRun = NetworkDiagnosticComposer.Compose(Inputs());
            Assert.Equal(D("pairing_no_account", ("radioName", RadioName)), noAccount.Capability.Lines[0]);
            Assert.Equal(D("pairing_not_signed_in", ("email", Account), ("radioName", RadioName)), notSignedIn.Capability.Lines[0]);
            Assert.Equal(D("capability_not_run", ("radioName", RadioName), ("email", Account)), notRun.Capability.Lines[0]);
            Assert.Null(notRun.SummaryLine);
        }

        [Fact]
        public void The_capability_part_always_says_it_measured_the_radios_site()
        {
            foreach (var s in new[]
            {
                NetworkDiagnosticComposer.Compose(Inputs()),
                NetworkDiagnosticComposer.Compose(Inputs(probe: Probe(true, true, true, true, true))),
                NetworkDiagnosticComposer.Compose(Inputs(probe: Probe(error: "x"))),
                NetworkDiagnosticComposer.Compose(Inputs(pairing: DiagnosticPairing.None)),
            })
            {
                Assert.Contains(D("capability_scope"), s.Capability.Lines);
                Assert.DoesNotContain(s.Capability.Lines, l => l.Contains("Your NAT", StringComparison.OrdinalIgnoreCase));
            }
        }

        [Theory]
        [InlineData(true, true, true, null, null, "summary_reachable_forwarded")]
        [InlineData(false, null, null, true, true, "summary_reachable_upnp")]
        [InlineData(false, null, null, false, false, "summary_possible_punch")]
        [InlineData(true, false, false, null, null, "summary_not_reachable")]
        [InlineData(null, true, true, true, true, "summary_unknown")]
        public void The_summary_line_names_what_the_rule_decided(bool? fwd, bool? fTcp, bool? fUdp, bool? uTcp, bool? uUdp, string key)
        {
            bool? nat = key == "summary_possible_punch" ? true : false;
            var s = NetworkDiagnosticComposer.Compose(Inputs(probe: Probe(fTcp, fUdp, uTcp, uUdp, nat), forwardingOn: fwd));
            Assert.Equal(D(key), s.SummaryLine);
            Assert.Contains(D(key), s.Capability.Lines);
        }

        // ── Policy, as it stands now ────────────────────────────────────

        [Fact]
        public void Policy_now_names_the_account_its_tier_and_whether_it_was_chosen()
        {
            var chosen = NetworkDiagnosticComposer.Compose(Inputs(tier: SmartLinkConnectionMode.ManualPlusUpnp, chosen: true, ports: Ports(ManualOk, UpnpOk)));
            var unchosen = NetworkDiagnosticComposer.Compose(Inputs(tier: SmartLinkConnectionMode.ManualPlusUpnp, chosen: false, ports: Ports(ManualOk, UpnpOk)));
            string tierName = SmartLinkTierOffers.TierName(SmartLinkConnectionMode.ManualPlusUpnp);
            Assert.Equal(D("policy_account_chosen", ("email", Account), ("radioName", RadioName), ("tierName", tierName)), chosen.PolicyNow.Lines[0]);
            Assert.Equal(D("policy_account_not_chosen", ("email", Account), ("radioName", RadioName), ("tierName", tierName)), unchosen.PolicyNow.Lines[0]);
        }

        [Theory]
        [InlineData(RadioConnectionPreference.Auto, "choice_automatic")]
        [InlineData(RadioConnectionPreference.ForwardOnly, "choice_forward_only")]
        [InlineData(RadioConnectionPreference.HolePunch, "choice_hole_punch")]
        public void Policy_now_names_the_radios_choice(RadioConnectionPreference choice, string key)
        {
            var s = NetworkDiagnosticComposer.Compose(Inputs(choice: choice, tier: SmartLinkConnectionMode.AutomaticHolePunch, ports: Ports(ManualOk, UpnpNone)));
            Assert.Contains(D("policy_choice", ("choiceName", D(key))), s.PolicyNow.Lines);
        }

        [Fact]
        public void Policy_now_says_the_method_order_the_policy_gives()
        {
            var s = NetworkDiagnosticComposer.Compose(Inputs(tier: SmartLinkConnectionMode.ManualPlusUpnp, ports: Ports(ManualOk, UpnpOk)));
            string methods = D("method_manual") + D("method_then") + D("method_upnp");
            Assert.Contains(D("policy_methods", ("methods", methods)), s.PolicyNow.Lines);
            Assert.Contains(D("policy_advertised",
                ("manual", D("pair_forwarded", ("tcp", 4994), ("udp", 4993))),
                ("upnp", D("pair_upnp", ("tcp", 21000), ("udp", 21001)))), s.PolicyNow.Lines);
            Assert.DoesNotContain(D("policy_punch_withheld"), s.PolicyNow.Lines);
        }

        [Fact]
        public void Policy_now_says_when_a_punch_after_ports_is_held_back()
        {
            var s = NetworkDiagnosticComposer.Compose(Inputs(tier: SmartLinkConnectionMode.AutomaticHolePunch, ports: Ports(ManualOk, UpnpNone)));
            Assert.Contains(D("policy_methods", ("methods", D("method_manual"))), s.PolicyNow.Lines);
            Assert.Contains(D("policy_punch_withheld"), s.PolicyNow.Lines);
        }

        [Fact]
        public void Policy_now_says_each_refusal_and_why()
        {
            string t1 = SmartLinkTierOffers.TierName(SmartLinkConnectionMode.ManualPortForwardOnly);
            string t2 = SmartLinkTierOffers.TierName(SmartLinkConnectionMode.ManualPlusUpnp);

            // Hole punch always under Tier 1.
            var punchAlways = NetworkDiagnosticComposer.Compose(Inputs(choice: RadioConnectionPreference.HolePunch, ports: Ports(ManualOk, UpnpNone)));
            Assert.Contains(D("policy_refused", ("reason", D("reason_punch_always", ("tierName", t1)))), punchAlways.PolicyNow.Lines);

            // Tier 1 and a UPnP-only radio: refused, and Tier 2 would admit it.
            var upnpOnly = NetworkDiagnosticComposer.Compose(Inputs(ports: Ports(ManualNone, UpnpOk)));
            Assert.Contains(D("policy_refused", ("reason", D("reason_tier", ("tierName", t1), ("lowestTierName", t2)))), upnpOnly.PolicyNow.Lines);

            // Forwarded ports only with nothing usable: no tier changes that.
            var forwardOnly = NetworkDiagnosticComposer.Compose(Inputs(choice: RadioConnectionPreference.ForwardOnly, ports: Ports(ManualNone, UpnpNone)));
            Assert.Contains(D("policy_refused", ("reason", D("reason_no_ports"))), forwardOnly.PolicyNow.Lines);

            string methodsPrefix = D("policy_methods", ("methods", "\u0001")).Split('\u0001')[0];
            foreach (var s in new[] { punchAlways, upnpOnly, forwardOnly })
                Assert.DoesNotContain(s.PolicyNow.Lines, l => l.StartsWith(methodsPrefix, StringComparison.Ordinal));
        }

        [Fact]
        public void Policy_now_with_no_known_account_or_ports_says_so()
        {
            var noAccount = NetworkDiagnosticComposer.Compose(Inputs(pairing: DiagnosticPairing.None, ports: Ports(ManualOk, UpnpOk)));
            Assert.Equal(new[] { D("policy_no_account", ("radioName", RadioName)) }, noAccount.PolicyNow.Lines);

            var noPorts = NetworkDiagnosticComposer.Compose(Inputs(ports: null));
            Assert.Contains(D("policy_ports_unknown", ("radioName", RadioName)), noPorts.PolicyNow.Lines);

            var fromRecord = NetworkDiagnosticComposer.Compose(Inputs(ports: Ports(ManualOk, UpnpNone), portsFromLastConnect: true));
            Assert.Contains(D("policy_advertised_at_last_connect",
                ("manual", D("pair_forwarded", ("tcp", 4994), ("udp", 4993))), ("upnp", D("pair_upnp_none"))), fromRecord.PolicyNow.Lines);
        }

        [Fact]
        public void A_not_signed_in_account_still_has_a_policy()
        {
            var s = NetworkDiagnosticComposer.Compose(Inputs(
                pairing: new DiagnosticPairing(DiagnosticPairingKind.AccountNotSignedIn, Account, "test"),
                ports: Ports(ManualOk, UpnpNone)));
            Assert.Contains(D("policy_methods", ("methods", D("method_manual"))), s.PolicyNow.Lines);
        }

        // ── Policy, as recorded at the last connect ─────────────────────

        [Fact]
        public void With_no_connect_the_recorded_policy_and_outcome_say_so()
        {
            var s = NetworkDiagnosticComposer.Compose(Inputs());
            Assert.Equal(new[] { D("no_connect_recorded", ("radioName", RadioName)) }, s.PolicyAtLastConnect.Lines);
            Assert.Equal(new[] { D("no_connect_recorded", ("radioName", RadioName)) }, s.Outcome.Lines);
        }

        [Fact]
        public void The_recorded_policy_is_the_records_not_todays()
        {
            var rec = Record(SmartLinkConnectionMode.ManualPlusUpnp, false, RadioConnectionPreference.ForwardOnly, Ports(ManualOk, UpnpOk), forcedPath: true);
            var s = NetworkDiagnosticComposer.Compose(Inputs(tier: SmartLinkConnectionMode.ManualPortForwardOnly, chosen: true,
                choice: RadioConnectionPreference.Auto, ports: Ports(ManualOk, UpnpOk), last: rec));
            var lines = s.PolicyAtLastConnect.Lines;
            string t2 = SmartLinkTierOffers.TierName(SmartLinkConnectionMode.ManualPlusUpnp);
            Assert.Contains(D("recorded_when", ("time", NetworkDiagnosticComposer.FormatTime(When))), lines);
            Assert.Contains(D("recorded_account_not_chosen", ("email", Account), ("tierName", t2)), lines);
            Assert.Contains(D("recorded_choice", ("choiceName", D("choice_forward_only"))), lines);
            Assert.Contains(D("recorded_forced_path"), lines);
            Assert.Contains(D("recorded_methods", ("methods", D("method_manual") + D("method_then") + D("method_upnp"))), lines);
            // And the change since is said.
            Assert.Contains(D("recorded_changed_since"), lines);
            // While today's policy is today's.
            Assert.Contains(D("policy_methods", ("methods", D("method_manual"))), s.PolicyNow.Lines);
        }

        [Fact]
        public void An_unchanged_policy_does_not_claim_a_change()
        {
            var rec = Record(SmartLinkConnectionMode.ManualPortForwardOnly, true, RadioConnectionPreference.Auto, Ports(ManualOk, UpnpNone));
            var s = NetworkDiagnosticComposer.Compose(Inputs(ports: Ports(ManualOk, UpnpNone), last: rec));
            Assert.DoesNotContain(D("recorded_changed_since"), s.PolicyAtLastConnect.Lines);
            Assert.DoesNotContain(D("recorded_forced_path"), s.PolicyAtLastConnect.Lines);
        }

        [Theory]
        [InlineData("tier")]
        [InlineData("chosen")]
        [InlineData("choice")]
        [InlineData("account")]
        public void Each_kind_of_change_since_the_last_connect_is_noticed(string what)
        {
            var rec = Record(SmartLinkConnectionMode.ManualPortForwardOnly, true, RadioConnectionPreference.Auto, Ports(ManualOk, UpnpNone));
            var inputs = Inputs(ports: Ports(ManualOk, UpnpNone), last: rec);
            inputs = what switch
            {
                "tier" => inputs with { Tier = SmartLinkConnectionMode.AutomaticHolePunch },
                "chosen" => inputs with { TierChosen = false },
                "choice" => inputs with { Choice = RadioConnectionPreference.ForwardOnly },
                _ => inputs with { Pairing = new DiagnosticPairing(DiagnosticPairingKind.Paired, "other@example.invalid", "test") },
            };
            Assert.Contains(D("recorded_changed_since"), NetworkDiagnosticComposer.Compose(inputs).PolicyAtLastConnect.Lines);
        }

        [Fact]
        public void A_refused_connect_is_recorded_as_refused_and_tried_nothing()
        {
            var rec = Record(SmartLinkConnectionMode.ManualPortForwardOnly, false, RadioConnectionPreference.Auto, Ports(ManualNone, UpnpOk));
            Assert.True(rec.IsRefused);
            var s = NetworkDiagnosticComposer.Compose(Inputs(last: rec, chosen: false, ports: Ports(ManualNone, UpnpOk)));
            string t1 = SmartLinkTierOffers.TierName(SmartLinkConnectionMode.ManualPortForwardOnly);
            string t2 = SmartLinkTierOffers.TierName(SmartLinkConnectionMode.ManualPlusUpnp);
            Assert.Contains(D("recorded_refused", ("reason", D("reason_tier", ("tierName", t1), ("lowestTierName", t2)))), s.PolicyAtLastConnect.Lines);
            Assert.Equal(new[] { D("outcome_refused", ("time", NetworkDiagnosticComposer.FormatTime(When))) }, s.Outcome.Lines);
        }

        // ── Outcome ─────────────────────────────────────────────────────

        [Fact]
        public void Every_way_an_attempt_can_end_has_its_own_sentence()
        {
            var seen = new HashSet<string>();
            foreach (SmartLinkAttemptEnd end in Enum.GetValues(typeof(SmartLinkAttemptEnd)))
            {
                string s = NetworkDiagnosticComposer.EndSentence(end);
                Assert.DoesNotContain("settings.network.diag.", s);
                Assert.NotEqual(D("end_unknown"), s);
                Assert.True(seen.Add(s), end + " shares its sentence with another end");
            }
            Assert.Equal(11, seen.Count);
        }

        [Fact]
        public void Each_attempt_is_said_with_its_method_ports_and_end()
        {
            var rec = Record(SmartLinkConnectionMode.ManualPlusUpnp, true, RadioConnectionPreference.Auto, Ports(ManualOk, UpnpOk))
                .WithAttempt(SmartLinkAttemptRecord.ForPorts(SmartLinkMethod.ManualPorts, ManualOk) with
                    { HandleArrived = true, OpenSucceeded = true, End = SmartLinkAttemptEnd.NoQualifiedUdp, EndDetail = "no VITA in 15 s" })
                .WithAttempt(SmartLinkAttemptRecord.ForPorts(SmartLinkMethod.UpnpPorts, UpnpOk) with
                    { HandleArrived = true, OpenSucceeded = true, QualifiedUdpAfter = TimeSpan.FromMilliseconds(118), End = SmartLinkAttemptEnd.Proven })
                .WithMethodUsed(SmartLinkMethod.UpnpPorts);
            var s = NetworkDiagnosticComposer.Compose(Inputs(tier: SmartLinkConnectionMode.ManualPlusUpnp, ports: Ports(ManualOk, UpnpOk), last: rec));
            var lines = s.Outcome.Lines;
            Assert.Equal(D("outcome_attempt_ports", ("n", 1), ("method", D("method_manual")), ("tcp", 4994), ("udp", 4993),
                ("end", NetworkDiagnosticComposer.EndSentence(SmartLinkAttemptEnd.NoQualifiedUdp))), lines[0]);
            Assert.Equal(D("outcome_attempt_ports", ("n", 2), ("method", D("method_upnp")), ("tcp", 21000), ("udp", 21001),
                ("end", NetworkDiagnosticComposer.EndSentence(SmartLinkAttemptEnd.Proven))), lines[1]);
            Assert.Equal(D("outcome_first_udp", ("n", 2), ("ms", "118")), lines[2]);
            Assert.Equal(D("outcome_used", ("method", D("method_upnp"))), lines[3]);
            Assert.Equal(4, lines.Count);
            // The trace detail never reaches the screen; the export carries it.
            Assert.DoesNotContain(lines, l => l.Contains("no VITA", StringComparison.Ordinal));
            Assert.Contains("no VITA in 15 s", s.ToMarkdown("S-1", When));
            Assert.DoesNotContain("no VITA", s.ToText());
        }

        [Fact]
        public void A_punch_attempt_names_its_port_and_a_connect_that_left_nothing_says_so()
        {
            var rec = Record(SmartLinkConnectionMode.AutomaticHolePunch, true, RadioConnectionPreference.HolePunch, Ports(ManualOk, UpnpNone))
                .WithAttempt(SmartLinkAttemptRecord.ForPunch(31234, "random") with { End = SmartLinkAttemptEnd.NoHandle });
            var s = NetworkDiagnosticComposer.Compose(Inputs(tier: SmartLinkConnectionMode.AutomaticHolePunch,
                choice: RadioConnectionPreference.HolePunch, ports: Ports(ManualOk, UpnpNone), last: rec));
            Assert.Equal(D("outcome_attempt_punch", ("n", 1), ("method", D("method_punch")), ("port", 31234),
                ("end", NetworkDiagnosticComposer.EndSentence(SmartLinkAttemptEnd.NoHandle))), s.Outcome.Lines[0]);
            Assert.Equal(D("outcome_none_used"), s.Outcome.Lines[^1]);
        }

        [Fact]
        public void A_connect_with_no_attempt_yet_says_so()
        {
            var rec = Record(SmartLinkConnectionMode.ManualPortForwardOnly, true, RadioConnectionPreference.Auto, Ports(ManualOk, UpnpNone));
            var s = NetworkDiagnosticComposer.Compose(Inputs(ports: Ports(ManualOk, UpnpNone), last: rec));
            Assert.Equal(new[] { D("outcome_no_attempt", ("time", NetworkDiagnosticComposer.FormatTime(When))) }, s.Outcome.Lines);
        }

        /// <summary>
        /// Codex's check, B1: a try that ends Dialled is a handoff. Its open is
        /// said only as the record has it, and the handed-off method is said
        /// to have made the connection in use only when its try was proven.
        /// </summary>
        [Fact]
        public void A_handoff_is_said_only_as_far_as_the_record_shows_it_went()
        {
            SmartLinkConnectRecord Handed(bool? opened, SmartLinkAttemptEnd end = SmartLinkAttemptEnd.Dialled) =>
                Record(SmartLinkConnectionMode.ManualPortForwardOnly, true, RadioConnectionPreference.Auto, Ports(ManualOk, UpnpNone))
                    .WithAttempt(SmartLinkAttemptRecord.ForPorts(SmartLinkMethod.ManualPorts, ManualOk) with
                        { HandleArrived = true, OpenSucceeded = opened, End = end })
                    .WithMethodUsed(SmartLinkMethod.ManualPorts);
            IReadOnlyList<string> Said(SmartLinkConnectRecord r) =>
                NetworkDiagnosticComposer.Compose(Inputs(ports: Ports(ManualOk, UpnpNone), last: r)).Outcome.Lines;
            string Try(string endKey) => D("outcome_attempt_ports", ("n", 1), ("method", D("method_manual")),
                ("tcp", 4994), ("udp", 4993), ("end", D(endKey)));
            string used = D("outcome_used", ("method", D("method_manual")));
            string handed = D("outcome_handed_unconfirmed", ("method", D("method_manual")));

            Assert.Equal(new[] { Try("end_dialled"), handed }, Said(Handed(null)));
            Assert.Equal(new[] { Try("end_dialled_opened"), handed }, Said(Handed(true)));
            Assert.Equal(new[] { Try("end_dialled_not_opened"), handed }, Said(Handed(false)));
            // A failure filed later (the attempt walk) is never a connection in use either.
            Assert.Equal(new[] { Try("end_open_failed"), handed }, Said(Handed(false, SmartLinkAttemptEnd.OpenFailed)));
            // Only a proven try earns the claim.
            Assert.Equal(new[] { Try("end_proven"), used }, Said(Handed(true, SmartLinkAttemptEnd.Proven)));

            // The three handoff sentences are distinct from each other and from every other end.
            var all = Enum.GetValues(typeof(SmartLinkAttemptEnd)).Cast<SmartLinkAttemptEnd>()
                .Select(NetworkDiagnosticComposer.EndSentence)
                .Concat(new[] { D("end_dialled_opened"), D("end_dialled_not_opened") }).ToList();
            Assert.Equal(all.Count, all.Distinct().Count());
            // An unconfirmed handoff claims no opened radio and no live connection.
            Assert.DoesNotContain("was opened", D("end_dialled"), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("in use", handed, StringComparison.OrdinalIgnoreCase);
        }

        // ── Rendering ───────────────────────────────────────────────────

        private static IEnumerable<NetworkDiagnosticSections> Variety()
        {
            var rec = Record(SmartLinkConnectionMode.ManualPlusUpnp, true, RadioConnectionPreference.Auto, Ports(ManualOk, UpnpOk))
                .WithAttempt(SmartLinkAttemptRecord.ForPorts(SmartLinkMethod.ManualPorts, ManualOk) with { End = SmartLinkAttemptEnd.ConnectFailed, EndDetail = "tcp" })
                .WithAttempt(SmartLinkAttemptRecord.ForPorts(SmartLinkMethod.UpnpPorts, UpnpOk) with { End = SmartLinkAttemptEnd.Dialled })
                .WithMethodUsed(SmartLinkMethod.UpnpPorts);
            yield return NetworkDiagnosticComposer.Compose(Inputs());
            yield return NetworkDiagnosticComposer.Compose(Inputs(pairing: DiagnosticPairing.None));
            yield return NetworkDiagnosticComposer.Compose(Inputs(probe: Probe(true, false, null, true, false), forwardingOn: null, ports: Ports(ManualOk, UpnpOk), last: rec));
            yield return NetworkDiagnosticComposer.Compose(Inputs(probe: Probe(error: "Timed out", timedOut: true), choice: RadioConnectionPreference.HolePunch, ports: Ports(ManualNone, UpnpNone)));
            yield return NetworkDiagnosticComposer.Compose(Inputs(probe: Probe(false, false, false, false, true), forwardingOn: false,
                tier: SmartLinkConnectionMode.AutomaticHolePunch, ports: Ports(ManualNone, UpnpNone), portsFromLastConnect: true, last: rec));
        }

        [Fact]
        public void Every_assembled_sentence_is_filled_and_none_is_a_key()
        {
            foreach (var s in Variety())
            foreach (var part in s.Parts)
            {
                Assert.DoesNotContain("settings.network.diag.", part.Heading);
                foreach (var line in part.Lines)
                {
                    Assert.DoesNotContain("settings.network.diag.", line);
                    Assert.DoesNotContain("{", line);
                    Assert.DoesNotContain("}", line);
                    Assert.False(string.IsNullOrWhiteSpace(line));
                }
            }
        }

        [Fact]
        public void The_three_parts_are_separate_and_policy_is_shown_twice_each_labelled()
        {
            var s = NetworkDiagnosticComposer.Compose(Inputs());
            var headings = s.Parts.Select(p => p.Heading).ToArray();
            Assert.Equal(new[] { D("capability_heading"), D("policy_now_heading"), D("policy_recorded_heading"), D("outcome_heading") }, headings);
            Assert.Equal(4, headings.Distinct().Count());
        }

        [Fact]
        public void The_on_screen_text_is_each_heading_then_its_lines()
        {
            var s = Variety().ElementAt(2);
            var text = s.ToText().Split(Environment.NewLine);
            foreach (var part in s.Parts)
            {
                int h = Array.IndexOf(text, part.Heading);
                Assert.True(h >= 0, part.Heading + " is not a line of its own");
                for (int i = 0; i < part.Lines.Count; i++) Assert.Equal(part.Lines[i], text[h + 1 + i]);
            }
        }

        [Fact]
        public void The_export_is_prose_and_bullets_with_policy_and_outcome_and_never_a_table()
        {
            foreach (var s in Variety())
            {
                string md = s.ToMarkdown("S-1", When);
                Assert.StartsWith("# " + D("export_title"), md);
                foreach (var part in s.Parts) Assert.Contains("## " + part.Heading, md);
                Assert.Contains("## " + D("policy_now_heading"), md);
                Assert.Contains("## " + D("outcome_heading"), md);
                foreach (var raw in md.Split('\n'))
                {
                    string line = raw.TrimEnd('\r');
                    Assert.False(line.TrimStart().StartsWith("|", StringComparison.Ordinal), "a table row: " + line);
                    Assert.DoesNotContain("---|", line);
                    Assert.DoesNotContain("|---", line);
                    Assert.True(line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith("- ", StringComparison.Ordinal),
                        "neither a heading nor a bullet: " + line);
                }
                Assert.Contains(D("export_radio", ("serial", "S-1")), md);
            }
        }
    }
}
