using System;
using System.Collections.Generic;
using Radios.ChainChecks;

namespace Radios.Fixer
{
    /// <summary>
    /// The host's transmitting stage executors: the only code in the Fixer Tool
    /// that both consults <see cref="FixerTransmitGate"/> and calls something
    /// that keys a radio.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The engine holds these as opaque <see cref="FixerStage.Execute"/>
    /// delegates and cannot see inside them. That is the boundary: the engine
    /// runs stages, the host decides whether RF may leave the radio.
    /// </para>
    /// <para>
    /// <b>A gate nothing consults is not a gate.</b> This file exists so that
    /// the guard is on the actual path rather than sitting beside it — which is
    /// the failure mode this whole tool was built to expose, applied to the tool
    /// itself.
    /// </para>
    /// <para>
    /// <b>No new vocabulary.</b> The answer and the evidence come from
    /// <see cref="TxTuneProbe.Explain"/> and
    /// <see cref="TxTuneProbe.EvidenceSection"/>, not from words invented here.
    /// Two descriptions of one measurement drift apart, and the operator ends up
    /// hearing one thing and mailing FlexRadio another.
    /// </para>
    /// </remarks>
    public static class FixerTransmitStages
    {
        /// <summary>
        /// Reads the live radio for the executor. Returns null when there is no
        /// radio — never a stand-in, because a stand-in would let the stage
        /// report a measurement nothing made.
        /// </summary>
        public delegate FlexBase RadioSource();

        /// <summary>
        /// Build the executor for the transmitter check: key the rig with TUNE
        /// and find out whether it makes power, with no audio involved at all.
        /// </summary>
        /// <remarks>
        /// Returns null when <paramref name="gate"/> or <paramref name="radio"/>
        /// is missing. Null is the engine's "the host wired nothing" signal, and
        /// it records the stage as unable to run — which is the honest outcome
        /// and, for a transmitting stage, is exactly what keeps a half-wired
        /// host from keying anything.
        /// </remarks>
        public static Func<FixerStageContext, FixerOutcome> TransmitterCheck(
            FixerTransmitGate gate, RadioSource radio)
        {
            if (gate == null || radio == null) return null;

            return ctx =>
            {
                FlexBase rig = Safely(radio);

                // Facts the gate is not allowed to take on trust, gathered here
                // and read from the radio itself.
                bool reachable = rig != null;
                bool keyed = ReadKeyed(rig);

                FixerTransmitGate.Decision d = gate.Request(
                    ctx.RunId, ctx.Stage?.Id, ctx.Stage?.Transmits ?? false, reachable, keyed);

                if (!d.Allowed) return OutcomeForRefusal(d);

                TxTuneProbe.Result r = TxTuneProbeRunner.Run(
                    rig,
                    // Re-derived from the gate's own record rather than passed
                    // as a bare true because we got this far. If the two ever
                    // disagree the runner refuses as well, and nothing keys.
                    loadDeclared: gate.LoadDeclaration.Length > 0,
                    cancel: ctx.Cancel,
                    antennaPort: null,
                    onKeyConfirmed: () => gate.NoteKeyed(ctx.Stage?.Id),
                    onUnkeyed: gate.NoteUnkeyed);

                return OutcomeFor(r, gate.LoadDeclaration);
            };
        }

        // -------- turning a refusal into something the operator hears --------

        /// <summary>
        /// What the operator gets when the gate said no. Public and pure so the
        /// wording and the taxonomy can be tested without a radio — the same
        /// reason <c>TxTuneProbe.CheckPreconditions</c> was lifted out of the
        /// runner. A decision buried inside a delegate that needs a transmitter
        /// to reach is a decision nothing will ever check.
        /// </summary>
        public static FixerOutcome OutcomeForRefusal(FixerTransmitGate.Decision d)
        {
            return new FixerOutcome
            {
                // The gate's words, unchanged. They were written to be spoken.
                Answer = d.Explanation,
                Findings = new[] { RefusalFinding(d) },
                Evidence = "Not run. " + d.Explanation,
            };
        }

        internal static FixerFinding RefusalFinding(FixerTransmitGate.Decision d)
        {
            // Who can put it right decides how the page renders it, so the
            // split has to be honest rather than tidy. Two of these are faults
            // in this software, and saying "here is what to do" about them
            // would be inventing an action the operator does not have.
            bool oursNotTheirs =
                d.Why == FixerTransmitGate.Refusal.TooFast ||
                d.Why == FixerTransmitGate.Refusal.StageDoesNotTransmit;

            return oursNotTheirs
                ? new FixerFinding(
                    "transmit-refused-" + Slug(d.Why),
                    FixOwner.NobodyHere,
                    d.Explanation,
                    "Nothing at your end caused this and nothing at your end will fix it. "
                    + "It is worth reporting, and the test identifier above is what to quote.")
                : new FixerFinding(
                    "transmit-refused-" + Slug(d.Why),
                    FixOwner.Operator,
                    d.Explanation,
                    WhatToDoAbout(d.Why));
        }

        private static string WhatToDoAbout(FixerTransmitGate.Refusal why)
        {
            switch (why)
            {
                case FixerTransmitGate.Refusal.LoadNotDeclared:
                    return "Say what the antenna socket is connected to, then run this step again.";
                case FixerTransmitGate.Refusal.NoRadio:
                    return "Connect to your radio, then run this step again.";
                case FixerTransmitGate.Refusal.AlreadyInFlight:
                    return "Wait for the radio to stop transmitting, then run this step again.";
                case FixerTransmitGate.Refusal.RunAborted:
                case FixerTransmitGate.Refusal.NoRun:
                case FixerTransmitGate.Refusal.WrongRun:
                case FixerTransmitGate.Refusal.BudgetSpent:
                    return "Start a new test.";
                case FixerTransmitGate.Refusal.StageAlreadyTransmitted:
                    return "Choose Run again if you meant to repeat this step.";
                default:
                    return "Start a new test.";
            }
        }

        // -------- turning a measurement into an answer --------

        /// <summary>
        /// Turn a finished measurement into the stage's answer, findings and
        /// evidence. Public and pure for the same reason as
        /// <see cref="OutcomeForRefusal"/>.
        /// </summary>
        public static FixerOutcome OutcomeFor(TxTuneProbe.Result r, string loadDeclaration)
        {
            var findings = new List<FixerFinding>();

            switch (r.Verdict)
            {
                case TxTuneProbe.Verdict.NoPower:
                    // Critical, and the wording matters more here than anywhere
                    // else in the tool: this is the operator being told to stop
                    // looking where they were looking.
                    findings.Add(new FixerFinding(
                        "transmitter-makes-no-power",
                        FixOwner.Operator,
                        "The transmitter was keyed and made no power at all, with no audio "
                        + "involved. Whatever is wrong, it is not your microphone.",
                        "Check what is connected to the antenna socket, which band you are on, "
                        + "whether the slice you are on is set to transmit, and whether anything "
                        + "is inhibiting transmit.",
                        critical: true));
                    break;

                case TxTuneProbe.Verdict.MakesPowerLoadSuspect:
                    findings.Add(new FixerFinding(
                        "antenna-port-suspect",
                        FixOwner.Operator,
                        "The transmitter works, but a large share of the power came back "
                        + "instead of going out.",
                        "Check what is connected to the antenna socket before reading anything "
                        + "into the audio measurements that follow."));
                    break;

                case TxTuneProbe.Verdict.NoForwardPowerMeter:
                    findings.Add(new FixerFinding(
                        "no-forward-power-meter",
                        FixOwner.NobodyHere,
                        "This radio did not report a forward power meter, so whether it made "
                        + "power cannot be said either way.",
                        "This is a gap in what could be measured, not a fault in your station. "
                        + "The steps after this one are read with that in mind."));
                    break;

                case TxTuneProbe.Verdict.NotRun:
                    findings.Add(new FixerFinding(
                        "transmitter-check-not-run-" + Slug(r.Skipped),
                        r.Skipped == TxTuneProbe.SkipReason.RadioNotReachable
                            ? FixOwner.Operator : FixOwner.NobodyHere,
                        TxTuneProbe.Explain(r),
                        r.Skipped == TxTuneProbe.SkipReason.RadioNotReachable
                            ? "Connect to your radio, then run this step again."
                            : "Nothing was measured, so the steps that follow are read without "
                              + "this one behind them."));
                    break;

                case TxTuneProbe.Verdict.MakesPower:
                default:
                    // Nothing wrong. No finding — a clean stage that manufactures
                    // a finding to look thorough is noise in a report someone has
                    // to read while something is broken.
                    break;
            }

            return new FixerOutcome
            {
                Answer = TxTuneProbe.Explain(r),
                Findings = findings,
                Evidence = WithLoad(TxTuneProbe.EvidenceSection(r), loadDeclaration),
                // The engine hands this back to a later stage untouched. Stage 4
                // reads it to know whether the transmitter had already been
                // proved good, which changes what an audio failure means.
                Payload = r,
            };
        }

        /// <summary>
        /// Put the operator's own words about the load into the evidence.
        /// FlexRadio will ask what the measurement was taken into, and a power
        /// reading with no stated load cannot be read by anyone later —
        /// including us.
        /// </summary>
        private static string WithLoad(string evidence, string load)
        {
            if (string.IsNullOrWhiteSpace(load)) return evidence ?? "";
            return (evidence ?? "").TrimEnd()
                + "\nAntenna socket, as stated by the operator: " + load;
        }

        // -------- plumbing --------

        private static FlexBase Safely(RadioSource radio)
        {
            try { return radio(); } catch { return null; }
        }

        private static bool ReadKeyed(FlexBase rig)
        {
            if (rig == null) return false;
            // A radio that cannot be asked is treated as KEYED, deliberately.
            // The two ways to be wrong here are not equal: refusing a transmit
            // that would have been fine costs a retry, and adding a transmit on
            // top of one already running does not.
            try { return rig.Transmit || rig.TxTune; } catch { return true; }
        }

        private static string Slug(object enumValue)
        {
            string s = enumValue?.ToString() ?? "unknown";
            var b = new System.Text.StringBuilder(s.Length + 4);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsUpper(c) && i > 0) b.Append('-');
                b.Append(char.ToLowerInvariant(c));
            }
            return b.ToString();
        }
    }
}
