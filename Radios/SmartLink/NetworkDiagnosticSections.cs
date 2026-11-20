#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Radios.SmartLink
{
    /// <summary>
    /// What SmartLink's network test says about the radio's site, judged by
    /// one rule: when the radio's forwarding is on, by its forwarded TCP and
    /// UDP results; otherwise by its UPnP TCP and UDP results. A hole punch is
    /// "possible" only when the radio's router supports one AND forwarding is
    /// off. Anything the test did not answer leaves the summary
    /// <see cref="Unknown"/> rather than guessing (the tiers plan, section 5).
    /// </summary>
    public enum CapabilitySummary
    {
        /// <summary>The test, or the radio's forwarding state, does not settle it.</summary>
        Unknown = 0,
        /// <summary>Both judged ports answered.</summary>
        ReachableByPorts = 1,
        /// <summary>The judged ports did not both answer, forwarding is off, and the radio's router supports a hole punch.</summary>
        PossibleByHolePunch = 2,
        /// <summary>The judged ports did not answer, and a hole punch is not on offer.</summary>
        NotReachable = 3,
    }

    /// <summary>One labelled part of the diagnostic: a heading and its sentences.</summary>
    public sealed record DiagnosticPart(string Heading, IReadOnlyList<string> Lines);

    /// <summary>
    /// Everything the diagnostic reads for one radio. Gathered by
    /// <c>FlexBase.NetworkDiagnosticInputsFor</c>; nothing here is a radio, a
    /// socket or a session, so the composition is testable from fakes.
    /// </summary>
    /// <param name="Serial">The radio the diagnostic describes.</param>
    /// <param name="RadioName">That radio's name, the operator's way.</param>
    /// <param name="Pairing">Which account brokers the radio, and whether its session is signed in.</param>
    /// <param name="Probe">The newest network test report for THIS radio from THAT account's session, or null.</param>
    /// <param name="RadioForwardingOn">The radio's own forwarding flag from the current radio list; null when no list describes it.</param>
    /// <param name="Tier">The brokering account's tier (Tier 1 when the account is not saved here).</param>
    /// <param name="TierChosen">Whether an operator chose that tier.</param>
    /// <param name="Choice">The radio's own connection choice.</param>
    /// <param name="PortsNow">The radio's advertised ports as they stand now; null when nothing describes them.</param>
    /// <param name="PortsFromLastConnect">True when <paramref name="PortsNow"/> came from the last connect's record rather than a current list.</param>
    /// <param name="LastConnect">The newest SmartLink connect record for this radio, this run; null when none.</param>
    public sealed record NetworkDiagnosticInputs(
        string Serial,
        string RadioName,
        DiagnosticPairing Pairing,
        NetworkDiagnosticReport? Probe,
        bool? RadioForwardingOn,
        SmartLinkConnectionMode Tier,
        bool TierChosen,
        RadioConnectionPreference Choice,
        RadioPortReport? PortsNow,
        bool PortsFromLastConnect,
        SmartLinkConnectRecord? LastConnect);

    /// <summary>
    /// The diagnostic's three separate things (the tiers plan, section 5),
    /// never merged into one verdict: CAPABILITY (what the radio's site
    /// measured), POLICY (shown twice, each labelled: as it stands now, and as
    /// recorded at the last connect, so a changed tier or choice is visible),
    /// and OUTCOME (what the last connect tried and how each attempt ended).
    /// Every sentence is a lexicon entry and DRAFT wording for the tiers
    /// words track.
    /// </summary>
    public sealed record NetworkDiagnosticSections(
        DiagnosticPart Capability,
        DiagnosticPart PolicyNow,
        DiagnosticPart PolicyAtLastConnect,
        DiagnosticPart Outcome,
        CapabilitySummary Summary,
        string? SummaryLine,
        IReadOnlyList<string> ExportOnlyDetails)
    {
        public IEnumerable<DiagnosticPart> Parts
        {
            get
            {
                yield return Capability;
                yield return PolicyNow;
                yield return PolicyAtLastConnect;
                yield return Outcome;
            }
        }

        /// <summary>
        /// The on-screen form: each part's heading on its own line, then its
        /// sentences, one per line, with a blank line between parts. Plain
        /// text so a screen reader can arrow through it line by line.
        /// </summary>
        public string ToText()
        {
            var sb = new StringBuilder();
            bool first = true;
            foreach (var part in Parts)
            {
                if (!first) sb.AppendLine();
                first = false;
                sb.AppendLine(part.Heading);
                foreach (var line in part.Lines) sb.AppendLine(line);
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// The Copy report and Save report form: a title, the radio, then each
        /// part as a second-level heading and bulleted sentences. Prose and
        /// bullets, never a table. The test's five raw results stay in the
        /// capability part, as before, and attempt details the screen does not
        /// show are bulleted under the outcome.
        /// </summary>
        public string ToMarkdown(string serial, DateTime nowUtc)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# " + Lexicon.Get("settings.network.diag.export_title"));
            sb.AppendLine();
            sb.AppendLine("- " + Lexicon.Get("settings.network.diag.export_radio",
                ("serial", string.IsNullOrWhiteSpace(serial) ? Lexicon.Get("settings.network.diag.state_unknown") : serial)));
            sb.AppendLine("- " + Lexicon.Get("settings.network.diag.export_written",
                ("time", nowUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))));
            foreach (var part in Parts)
            {
                sb.AppendLine();
                sb.AppendLine("## " + part.Heading);
                sb.AppendLine();
                foreach (var line in part.Lines) sb.AppendLine("- " + line);
                if (ReferenceEquals(part, Outcome))
                    foreach (var detail in ExportOnlyDetails) sb.AppendLine("- " + detail);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Composes <see cref="NetworkDiagnosticSections"/> from
    /// <see cref="NetworkDiagnosticInputs"/>. Pure: reads the lexicon and
    /// nothing else.
    /// </summary>
    public static class NetworkDiagnosticComposer
    {
        private const string K = "settings.network.diag.";

        /// <summary>The time a diagnostic line shows: local, to the second.</summary>
        public static string FormatTime(DateTime utc) =>
            (utc.Kind == DateTimeKind.Local ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime())
                .ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        /// <summary>
        /// The capability summary by the one rule (see <see cref="CapabilitySummary"/>).
        /// An incomplete probe, an unknown forwarding state, or an unanswered
        /// result that would decide it, all leave it <see cref="CapabilitySummary.Unknown"/>.
        /// </summary>
        public static CapabilitySummary Summarize(NetworkDiagnosticReport? probe, bool? radioForwardingOn)
        {
            if (probe == null || !probe.ProbeCompleted || radioForwardingOn is not bool forwarding)
                return CapabilitySummary.Unknown;

            bool? tcp = forwarding ? probe.ManualForwardTcpReachable : probe.UpnpTcpReachable;
            bool? udp = forwarding ? probe.ManualForwardUdpReachable : probe.UpnpUdpReachable;

            if (tcp == true && udp == true) return CapabilitySummary.ReachableByPorts;
            // Neither port failed but one is unanswered: the ports may yet be fine.
            if (tcp != false && udp != false) return CapabilitySummary.Unknown;

            // A judged port failed. A punch is offered only with forwarding off.
            if (forwarding) return CapabilitySummary.NotReachable;
            return probe.NatSupportsHolePunch switch
            {
                true => CapabilitySummary.PossibleByHolePunch,
                false => CapabilitySummary.NotReachable,
                null => CapabilitySummary.Unknown,
            };
        }

        public static NetworkDiagnosticSections Compose(NetworkDiagnosticInputs input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            var summary = Summarize(input.Probe, input.RadioForwardingOn);
            string? summaryLine = input.Probe != null && input.Probe.ProbeCompleted
                ? SummaryLine(summary, input.RadioForwardingOn)
                : null;
            var details = new List<string>();
            return new NetworkDiagnosticSections(
                Capability(input, summaryLine),
                PolicyNow(input),
                PolicyAtLastConnect(input),
                Outcome(input, details),
                summary,
                summaryLine,
                details);
        }

        // ── Capability ───────────────────────────────────────────────────

        private static DiagnosticPart Capability(NetworkDiagnosticInputs input, string? summaryLine)
        {
            var lines = new List<string>();
            var probe = input.Probe;
            var pairing = input.Pairing;
            if (probe == null)
            {
                lines.Add(pairing.Kind switch
                {
                    DiagnosticPairingKind.NoBrokeringAccount =>
                        Lexicon.Get(K + "pairing_no_account", ("radioName", input.RadioName)),
                    DiagnosticPairingKind.AccountNotSignedIn =>
                        Lexicon.Get(K + "pairing_not_signed_in", ("email", pairing.Account), ("radioName", input.RadioName)),
                    _ => Lexicon.Get(K + "capability_not_run", ("radioName", input.RadioName), ("email", pairing.Account)),
                });
            }
            else if (!probe.ProbeCompleted)
            {
                lines.Add(probe.TimedOut
                    ? Lexicon.Get(K + "capability_timed_out", ("time", FormatTime(probe.TimestampUtc)))
                    : Lexicon.Get(K + "capability_did_not_complete",
                        ("time", FormatTime(probe.TimestampUtc)), ("detail", probe.ErrorDetail ?? "")));
            }
            else
            {
                lines.Add(Lexicon.Get(K + "capability_tested_at",
                    ("time", FormatTime(probe.TimestampUtc)), ("email", pairing.Account)));
                if (summaryLine != null) lines.Add(summaryLine);
                lines.Add(Lexicon.Get(K + "capability_forwarded",
                    ("tcp", Reach(probe.ManualForwardTcpReachable)), ("udp", Reach(probe.ManualForwardUdpReachable))));
                lines.Add(Lexicon.Get(K + "capability_upnp",
                    ("tcp", Reach(probe.UpnpTcpReachable)), ("udp", Reach(probe.UpnpUdpReachable))));
                lines.Add(Lexicon.Get(K + "capability_punch", ("state", YesNo(probe.NatSupportsHolePunch))));
                lines.Add(input.RadioForwardingOn switch
                {
                    true => Lexicon.Get(K + "capability_forwarding_on"),
                    false => Lexicon.Get(K + "capability_forwarding_off"),
                    null => Lexicon.Get(K + "capability_forwarding_unknown"),
                });
            }
            // The label rule: this is the RADIO's site, never the operator's.
            lines.Add(Lexicon.Get(K + "capability_scope"));
            return new DiagnosticPart(Lexicon.Get(K + "capability_heading"), lines);
        }

        private static string SummaryLine(CapabilitySummary summary, bool? forwardingOn) => summary switch
        {
            CapabilitySummary.ReachableByPorts => forwardingOn == true
                ? Lexicon.Get(K + "summary_reachable_forwarded")
                : Lexicon.Get(K + "summary_reachable_upnp"),
            CapabilitySummary.PossibleByHolePunch => Lexicon.Get(K + "summary_possible_punch"),
            CapabilitySummary.NotReachable => Lexicon.Get(K + "summary_not_reachable"),
            _ => Lexicon.Get(K + "summary_unknown"),
        };

        private static string Reach(bool? v) => v switch
        {
            true => Lexicon.Get(K + "state_reachable"),
            false => Lexicon.Get(K + "state_not_reachable"),
            null => Lexicon.Get(K + "state_unknown"),
        };

        private static string YesNo(bool? v) => v switch
        {
            true => Lexicon.Get(K + "state_yes"),
            false => Lexicon.Get(K + "state_no"),
            null => Lexicon.Get(K + "state_unknown"),
        };

        // ── Policy, as it stands now ─────────────────────────────────────

        private static DiagnosticPart PolicyNow(NetworkDiagnosticInputs input)
        {
            var lines = new List<string>();
            if (input.Pairing.Kind == DiagnosticPairingKind.NoBrokeringAccount)
            {
                lines.Add(Lexicon.Get(K + "policy_no_account", ("radioName", input.RadioName)));
                return new DiagnosticPart(Lexicon.Get(K + "policy_now_heading"), lines);
            }

            lines.Add(Lexicon.Get(input.TierChosen ? K + "policy_account_chosen" : K + "policy_account_not_chosen",
                ("email", input.Pairing.Account), ("radioName", input.RadioName),
                ("tierName", SmartLinkTierOffers.TierName(input.Tier))));
            lines.Add(Lexicon.Get(K + "policy_choice", ("choiceName", ChoiceName(input.Choice))));

            if (input.PortsNow == null)
            {
                lines.Add(Lexicon.Get(K + "policy_ports_unknown", ("radioName", input.RadioName)));
                return new DiagnosticPart(Lexicon.Get(K + "policy_now_heading"), lines);
            }

            lines.Add(Lexicon.Get(input.PortsFromLastConnect ? K + "policy_advertised_at_last_connect" : K + "policy_advertised",
                ("manual", PairPhrase(input.PortsNow.Manual)), ("upnp", PairPhrase(input.PortsNow.Upnp))));

            var decision = SmartLinkMethodPolicy.Decide(new SmartLinkPolicyInput(
                input.Tier, input.TierChosen, input.Choice, ForcedPath: false, input.PortsNow));
            if (decision.IsRefused)
            {
                lines.Add(Lexicon.Get(K + "policy_refused",
                    ("reason", RefusalReason(decision.Refusal, input.Tier, decision.LowestAdmittingTier))));
            }
            else
            {
                lines.Add(Lexicon.Get(K + "policy_methods", ("methods", MethodList(decision.Methods))));
                if (decision.PunchWithheldUntilGate) lines.Add(Lexicon.Get(K + "policy_punch_withheld"));
            }
            lines.Add(Lexicon.Get(K + "policy_smartlink_only"));
            return new DiagnosticPart(Lexicon.Get(K + "policy_now_heading"), lines);
        }

        // ── Policy, as recorded at the last connect ──────────────────────

        private static DiagnosticPart PolicyAtLastConnect(NetworkDiagnosticInputs input)
        {
            var lines = new List<string>();
            var rec = input.LastConnect;
            if (rec == null)
            {
                lines.Add(Lexicon.Get(K + "no_connect_recorded", ("radioName", input.RadioName)));
                return new DiagnosticPart(Lexicon.Get(K + "policy_recorded_heading"), lines);
            }

            lines.Add(Lexicon.Get(K + "recorded_when", ("time", FormatTime(rec.StartedUtc))));
            lines.Add(Lexicon.Get(rec.TierChosen ? K + "recorded_account_chosen" : K + "recorded_account_not_chosen",
                ("email", rec.BrokeringAccount), ("tierName", SmartLinkTierOffers.TierName(rec.Tier))));
            lines.Add(Lexicon.Get(K + "recorded_choice", ("choiceName", ChoiceName(rec.PerRadioChoice))));
            if (rec.ForcedPath) lines.Add(Lexicon.Get(K + "recorded_forced_path"));
            lines.Add(Lexicon.Get(K + "recorded_advertised",
                ("manual", PairPhrase(rec.Report.Manual)), ("upnp", PairPhrase(rec.Report.Upnp))));
            if (rec.IsRefused)
            {
                lines.Add(Lexicon.Get(K + "recorded_refused",
                    ("reason", RefusalReason(rec.Refusal, rec.Tier, rec.LowestAdmittingTier))));
            }
            else
            {
                lines.Add(Lexicon.Get(K + "recorded_methods", ("methods", MethodList(rec.MethodOrder))));
                if (rec.PunchWithheldUntilGate) lines.Add(Lexicon.Get(K + "recorded_punch_withheld"));
            }

            // A changed tier, chosen flag or choice since then is said, so the
            // two policies are never read as the same thing by accident.
            if (input.Pairing.Kind != DiagnosticPairingKind.NoBrokeringAccount
                && (!string.Equals(rec.BrokeringAccount, input.Pairing.Account, StringComparison.OrdinalIgnoreCase)
                    || rec.Tier != input.Tier || rec.TierChosen != input.TierChosen || rec.PerRadioChoice != input.Choice))
            {
                lines.Add(Lexicon.Get(K + "recorded_changed_since"));
            }
            return new DiagnosticPart(Lexicon.Get(K + "policy_recorded_heading"), lines);
        }

        // ── Outcome ──────────────────────────────────────────────────────

        private static DiagnosticPart Outcome(NetworkDiagnosticInputs input, List<string> exportDetails)
        {
            var lines = new List<string>();
            var rec = input.LastConnect;
            if (rec == null)
            {
                lines.Add(Lexicon.Get(K + "no_connect_recorded", ("radioName", input.RadioName)));
                return new DiagnosticPart(Lexicon.Get(K + "outcome_heading"), lines);
            }
            if (rec.IsRefused)
            {
                lines.Add(Lexicon.Get(K + "outcome_refused", ("time", FormatTime(rec.StartedUtc))));
                return new DiagnosticPart(Lexicon.Get(K + "outcome_heading"), lines);
            }
            if (rec.Attempts.Count == 0)
            {
                lines.Add(Lexicon.Get(K + "outcome_no_attempt", ("time", FormatTime(rec.StartedUtc))));
                return new DiagnosticPart(Lexicon.Get(K + "outcome_heading"), lines);
            }

            for (int i = 0; i < rec.Attempts.Count; i++)
            {
                var a = rec.Attempts[i];
                int n = a.Sequence > 0 ? a.Sequence : i + 1;
                string end = EndSentence(a.End);
                lines.Add(a.Method == SmartLinkMethod.HolePunch
                    ? Lexicon.Get(K + "outcome_attempt_punch",
                        ("n", n), ("method", MethodName(a.Method)), ("port", a.PunchPortSent), ("end", end))
                    : Lexicon.Get(K + "outcome_attempt_ports",
                        ("n", n), ("method", MethodName(a.Method)), ("tcp", a.TlsPortSent), ("udp", a.UdpPortSent), ("end", end)));
                if (a.QualifiedUdpAfter is TimeSpan q)
                    lines.Add(Lexicon.Get(K + "outcome_first_udp",
                        ("n", n), ("ms", ((long)q.TotalMilliseconds).ToString(CultureInfo.InvariantCulture))));
                // EndDetail is plain words for the trace; never shown or spoken
                // as-is (the frozen record's contract), so the export carries it.
                if (!string.IsNullOrWhiteSpace(a.EndDetail))
                    exportDetails.Add(Lexicon.Get(K + "export_attempt_detail", ("n", n), ("detail", a.EndDetail)));
            }

            lines.Add(rec.MethodUsed is SmartLinkMethod used
                ? Lexicon.Get(K + "outcome_used", ("method", MethodName(used)))
                : Lexicon.Get(K + "outcome_none_used"));
            return new DiagnosticPart(Lexicon.Get(K + "outcome_heading"), lines);
        }

        /// <summary>One sentence per way an attempt can end; every value of <see cref="SmartLinkAttemptEnd"/> has its own.</summary>
        public static string EndSentence(SmartLinkAttemptEnd end) => Lexicon.Get(end switch
        {
            SmartLinkAttemptEnd.Pending => K + "end_pending",
            SmartLinkAttemptEnd.Proven => K + "end_proven",
            SmartLinkAttemptEnd.Dialled => K + "end_dialled",
            SmartLinkAttemptEnd.NoHandle => K + "end_no_handle",
            SmartLinkAttemptEnd.NullHandle => K + "end_null_handle",
            SmartLinkAttemptEnd.ConnectFailed => K + "end_connect_failed",
            SmartLinkAttemptEnd.OpenFailed => K + "end_open_failed",
            SmartLinkAttemptEnd.CommandConnectionLost => K + "end_command_lost",
            SmartLinkAttemptEnd.NoQualifiedUdp => K + "end_no_udp",
            SmartLinkAttemptEnd.Cancelled => K + "end_cancelled",
            SmartLinkAttemptEnd.NotSent => K + "end_not_sent",
            _ => K + "end_unknown",
        });

        // ── Shared phrases ───────────────────────────────────────────────

        public static string ChoiceName(RadioConnectionPreference choice) => Lexicon.Get(choice switch
        {
            RadioConnectionPreference.ForwardOnly => K + "choice_forward_only",
            RadioConnectionPreference.HolePunch => K + "choice_hole_punch",
            _ => K + "choice_automatic",
        });

        public static string MethodName(SmartLinkMethod method) => Lexicon.Get(method switch
        {
            SmartLinkMethod.UpnpPorts => K + "method_upnp",
            SmartLinkMethod.HolePunch => K + "method_punch",
            _ => K + "method_manual",
        });

        private static string MethodList(IReadOnlyList<SmartLinkMethod> methods) =>
            string.Join(Lexicon.Get(K + "method_then"), methods.Select(MethodName));

        private static string PairPhrase(AdvertisedPortPair pair) => pair.IsUsable
            ? Lexicon.Get(pair.Source == PortPairSource.Upnp ? K + "pair_upnp" : K + "pair_forwarded",
                ("tcp", pair.TlsPort), ("udp", pair.UdpPort))
            : Lexicon.Get(pair.Source == PortPairSource.Upnp ? K + "pair_upnp_none" : K + "pair_forwarded_none");

        private static string RefusalReason(SmartLinkRefusal refusal, SmartLinkConnectionMode tier,
            SmartLinkConnectionMode? lowest) => refusal switch
        {
            SmartLinkRefusal.HolePunchAlwaysButTierForbidsPunch =>
                Lexicon.Get(K + "reason_punch_always", ("tierName", SmartLinkTierOffers.TierName(tier))),
            SmartLinkRefusal.TierForbidsWhatTheRadioOffers when lowest is SmartLinkConnectionMode l =>
                Lexicon.Get(K + "reason_tier", ("tierName", SmartLinkTierOffers.TierName(tier)),
                    ("lowestTierName", SmartLinkTierOffers.TierName(l))),
            SmartLinkRefusal.TierForbidsWhatTheRadioOffers =>
                Lexicon.Get(K + "reason_tier_no_higher", ("tierName", SmartLinkTierOffers.TierName(tier))),
            _ => Lexicon.Get(K + "reason_no_ports"),
        };
    }
}
