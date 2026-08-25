using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Radios.ChainChecks;

namespace Radios.Fixer
{
    /// <summary>Stage 1's facts: what the existing microphone measurement
    /// (#36) found. The measurement itself is the host's — this carries its
    /// result, and above all its VERDICT, which is decided by the measurement
    /// and not re-derived here.</summary>
    public sealed class MicCheckFacts
    {
        /// <summary>Did a measurement actually happen?</summary>
        public bool Measured { get; set; }

        /// <summary>The measurement's own verdict: did sound arrive?</summary>
        public bool AudioArrived { get; set; }

        public string Device { get; set; } = "";
        public string HostApi { get; set; } = "";

        /// <summary>dBFS. NaN when not measured.</summary>
        public double PeakDb { get; set; } = double.NaN;

        /// <summary>dBFS. NaN when not measured.</summary>
        public double NoiseFloorDb { get; set; } = double.NaN;

        /// <summary>Whatever else the measurement reported, verbatim.</summary>
        public string Detail { get; set; } = "";
    }

    /// <summary>Stage 3's facts: the injected probes as the host ran them.
    /// The probe set's judgement already exists (<see cref="TxProbeSet"/>);
    /// this hands it the results rather than re-deciding.</summary>
    public sealed class InjectedTransmitFacts
    {
        public IReadOnlyList<TxProbeSet.ProbeResult> Probes { get; set; }
            = Array.Empty<TxProbeSet.ProbeResult>();

        /// <summary>Is the transmit conditioning chain on? Null: not read —
        /// and the explanation must then hedge rather than name it.</summary>
        public bool? ConditioningActive { get; set; }

        public string Detail { get; set; } = "";
    }

    /// <summary>Stage 4's facts: the operator spoke; did it reach the radio?</summary>
    public sealed class SpokenTransmitFacts
    {
        /// <summary>Did the spoken check produce a measurement at all?</summary>
        public bool Attempted { get; set; }

        public bool ReachedRadio { get; set; }
        public string Device { get; set; } = "";
        public string HostApi { get; set; } = "";
        public string Detail { get; set; } = "";
    }

    /// <summary>
    /// The transmit stages' decisions — pure, so every branch an operator can
    /// be told is testable without a radio, a microphone or a WebView.
    /// </summary>
    public static class TransmitStages
    {
        // ---- stage 1: microphone ----

        public static FixerOutcome Microphone(MicCheckFacts facts)
        {
            if (facts == null) throw new ArgumentNullException(nameof(facts));

            string answer;
            var findings = new List<FixerFinding>();

            if (!facts.Measured)
            {
                answer = "The microphone was not measured — the measurement did not produce "
                       + "a result, so whether sound is arriving cannot be said either way.";
            }
            else if (facts.AudioArrived)
            {
                answer = "Yes — sound from " + NameOr(facts.Device, "your microphone")
                       + (facts.HostApi.Length > 0 ? ", on " + facts.HostApi : "")
                       + ", is arriving in this computer. No radio was involved in this "
                       + "check, so this stands on its own whatever happens later.";
            }
            else
            {
                answer = "No — the measurement ran and heard nothing above the noise floor "
                       + "from " + NameOr(facts.Device, "your microphone") + ".";
                findings.Add(new FixerFinding("mic-silent", FixOwner.Operator,
                    "Your microphone was measured and nothing arrived.",
                    "Check the cable, the Windows mute, and the Windows microphone privacy "
                    + "setting, then run this stage again."));
            }

            return new FixerOutcome
            {
                Answer = answer,
                Findings = findings,
                Evidence = MicEvidence(facts),
                // The whole point of keeping this: stage 4 is read against it.
                Payload = facts,
            };
        }

        private static string MicEvidence(MicCheckFacts f)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Microphone, no radio involved");
            sb.AppendLine("-----------------------------");
            sb.AppendLine("Measured: " + (f.Measured ? "yes" : "no"));
            sb.AppendLine("Device: " + NameOr(f.Device, "not reported"));
            sb.AppendLine("Host API: " + NameOr(f.HostApi, "not reported"));
            sb.AppendLine("Peak: " + Db(f.PeakDb));
            sb.AppendLine("Noise floor: " + Db(f.NoiseFloorDb));
            if (f.Detail.Length > 0) sb.AppendLine(f.Detail.TrimEnd());
            return sb.ToString();
        }

        // ---- stage 2: transmitter ----

        /// <summary>
        /// Reads a tune-probe result into a stage outcome. The probe itself —
        /// including keying the radio — belongs to the host; the verdicts,
        /// wording and evidence layout belong to <see cref="TxTuneProbe"/>
        /// and are reused rather than restated. A NotRun result may already
        /// carry the host's own refusal words (SkipDetail), and Explain
        /// prefers them — nothing here overwrites them with a generic line.
        /// </summary>
        /// <param name="loadDeclaration">
        /// What the operator said the antenna socket is connected to, from the
        /// host's gate. It travels with the evidence because a power reading
        /// with no stated load cannot be read afterwards by anyone — FlexRadio
        /// will ask what the measurement was taken into (#188).
        /// </param>
        public static FixerOutcome Transmitter(TxTuneProbe.Result probe,
                                               string loadDeclaration = null)
        {
            var findings = new List<FixerFinding>();

            switch (probe.Verdict)
            {
                case TxTuneProbe.Verdict.NoPower:
                    // THE critical interrupt of the whole tool. The operator
                    // came here because transmit audio does not work, and this
                    // one sentence redirects the entire session away from
                    // microphones. Criticality was the other way round until
                    // 2026-08-25 — load-suspect was marked critical and this
                    // was not — and that had it backwards: a suspect load
                    // still means the transmitter WORKS, and the probe already
                    // drops the carrier early on a bad reading, so the
                    // immediate hazard is handled before any words render.
                    findings.Add(new FixerFinding("tx-no-power", FixOwner.Operator,
                        "This is not an audio problem, and no amount of microphone testing "
                        + "will find it. The transmitter was asked to key its own carrier "
                        + "with nothing of yours in the path, and no RF came out, so the "
                        + "fault is upstream of anything the remaining stages measure.",
                        "Check the antenna connection, the band, whether the slice is set "
                        + "to transmit, and whether anything is inhibiting transmit. Then "
                        + "run this stage again.",
                        critical: true));
                    break;

                case TxTuneProbe.Verdict.MakesPowerLoadSuspect:
                    // Reported, not shouted. An assertive region that fires
                    // for everything is one an operator learns to ignore.
                    findings.Add(new FixerFinding("tx-load-suspect", FixOwner.Operator,
                        "The transmitter works, but a large share of its power came straight "
                        + "back instead of going out.",
                        "Check what is connected to the antenna port before transmitting "
                        + "again."));
                    break;
            }

            string evidence = TxTuneProbe.EvidenceSection(probe);
            if (!string.IsNullOrWhiteSpace(loadDeclaration))
                evidence += "Antenna socket, as stated by the operator: "
                          + loadDeclaration.Trim() + Environment.NewLine;

            return new FixerOutcome
            {
                Answer = TxTuneProbe.Explain(probe),
                Findings = findings,
                Evidence = evidence,
                Payload = probe,
            };
        }

        // ---- stage 3: injected transmit ----

        public static FixerOutcome Injected(InjectedTransmitFacts facts)
        {
            if (facts == null) throw new ArgumentNullException(nameof(facts));

            var sb = new StringBuilder();
            sb.AppendLine("Injected transmit, microphone bypassed");
            sb.AppendLine("--------------------------------------");
            foreach (TxProbeSet.ProbeResult p in facts.Probes)
            {
                sb.Append(TxProbeSet.Name(p.Probe)).Append(": ").Append(p.Outcome);
                if (p.Detail.Length > 0) sb.Append(" — ").Append(p.Detail);
                sb.AppendLine();
            }
            sb.AppendLine("Conditioning chain: " + (facts.ConditioningActive == null
                ? "could not be read"
                : facts.ConditioningActive.Value ? "on" : "off"));
            if (facts.Detail.Length > 0) sb.AppendLine(facts.Detail.TrimEnd());

            return new FixerOutcome
            {
                Answer = TxProbeSet.OperatorSummary(facts.Probes, facts.ConditioningActive),
                Evidence = sb.ToString(),
                Payload = facts,
            };
        }

        // ---- stage 4: spoken transmit ----

        /// <summary>
        /// The spoken stage's answer, READ AGAINST the microphone baseline.
        /// Stages 3 and 4 differ in exactly one thing — the microphone — and
        /// a stage-4 failure means something quite different depending on
        /// whether that microphone measured well earlier. So the baseline is
        /// not a gate; it is half of the conclusion.
        /// </summary>
        public static FixerOutcome Spoken(SpokenTransmitFacts facts, MicCheckFacts micBaseline)
        {
            if (facts == null) throw new ArgumentNullException(nameof(facts));

            string answer;
            if (!facts.Attempted)
            {
                answer = "The spoken check did not produce a measurement, so whether your "
                       + "voice reaches the radio cannot be said either way.";
            }
            else if (facts.ReachedRadio)
            {
                answer = "Yes — your voice, through " + NameOr(facts.Device, "your microphone")
                       + ", reached the radio.";
            }
            else if (micBaseline != null && micBaseline.Measured && micBaseline.AudioArrived)
            {
                answer = "No — your voice did not reach the radio. But when the microphone "
                       + "check ran, sound from " + NameOr(micBaseline.Device, "that microphone")
                       + " WAS arriving in this computer, so the microphone itself is the "
                       + "least likely culprit. The difference lies between this computer "
                       + "and the radio — and the injected check just walked that same path, "
                       + "so read the two side by side.";
            }
            else if (micBaseline != null && micBaseline.Measured && !micBaseline.AudioArrived)
            {
                answer = "No — your voice did not reach the radio, and the microphone check "
                       + "heard nothing either. Start at the microphone: until sound arrives "
                       + "in this computer, nothing further along can carry it.";
            }
            else
            {
                answer = "No — your voice did not reach the radio, and because the microphone "
                       + "check was not run, whether the microphone or the path beyond it is "
                       + "at fault cannot be separated. Run the microphone check; it splits "
                       + "this question in two.";
            }

            var sb = new StringBuilder();
            sb.AppendLine("Spoken transmit, microphone in the path");
            sb.AppendLine("---------------------------------------");
            sb.AppendLine("Attempted: " + (facts.Attempted ? "yes" : "no"));
            sb.AppendLine("Reached the radio: " + (facts.Attempted
                ? (facts.ReachedRadio ? "yes" : "no") : "not measured"));
            sb.AppendLine("Device: " + NameOr(facts.Device, "not reported"));
            sb.AppendLine("Host API: " + NameOr(facts.HostApi, "not reported"));
            sb.AppendLine("Microphone baseline: " + (micBaseline == null
                ? "none — the microphone check was not run"
                : micBaseline.Measured
                    ? (micBaseline.AudioArrived
                        ? "sound was arriving from " + NameOr(micBaseline.Device, "the microphone")
                        : "measured, and nothing arrived")
                    : "attempted, but nothing was measured"));
            if (facts.Detail.Length > 0) sb.AppendLine(facts.Detail.TrimEnd());

            return new FixerOutcome
            {
                Answer = answer,
                Evidence = sb.ToString(),
                Payload = facts,
            };
        }

        // ---- helpers ----

        private static string NameOr(string name, string fallback)
            => string.IsNullOrWhiteSpace(name) ? fallback : name;

        private static string Db(double v)
            => double.IsNaN(v) ? "not measured"
                               : v.ToString("0.#", CultureInfo.InvariantCulture) + " dBFS";
    }
}
