using System;
using System.Collections.Generic;
using System.Linq;

namespace Radios.ChainChecks
{
    /// <summary>
    /// Three independent probes down the transmit chain, judged together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Noel, 2026-08-25:</b> "It might be actually good to do the three
    /// tests — tones, one tone, and TTS voice if available. Because if you got
    /// two out of three, that is enough."
    /// </para>
    /// <para>
    /// The point is not redundancy. It is that the three probes FAIL
    /// DIFFERENTLY, so agreement between them is worth more than any one of
    /// them repeated, and DISAGREEMENT is itself the most informative result
    /// the set can produce.
    /// </para>
    /// <para>
    /// <b>What each one is sensitive to that the others are not.</b> The single
    /// tone is the simplest signal there is: if anything at all gets through,
    /// this does, which makes it the last thing to fail and therefore the
    /// cleanest evidence that a chain is dead. The ladder is the only one that
    /// can see frequency — a filter in the wrong place, an equaliser nobody
    /// asked for, a microphone with a strong response of its own. The voice is
    /// the only one shaped like speech, so it is the only one that travels the
    /// CONDITIONING chain honestly: a noise gate or a speech-trained noise
    /// reducer treats a steady sine and a talking human as different problems,
    /// and the tone probes deliberately bypass conditioning because a
    /// calibrated reference must arrive unmodified.
    /// </para>
    /// <para>
    /// <b>So a split is a finding, not a tie to be broken.</b> Averaging three
    /// probes into one number would throw away the only thing three probes buy
    /// over one. Where the pattern points somewhere specific this says so, and
    /// where it does not it says that instead.
    /// </para>
    /// <para>
    /// <b>Two available probes that disagree are one-all, not a majority.</b>
    /// With no voice on the machine there are only two, and if they differ
    /// there is no vote to win. Reporting a winner there would be inventing
    /// confidence.
    /// </para>
    /// </remarks>
    public static class TxProbeSet
    {
        /// <summary>Which probe.</summary>
        public enum Probe
        {
            /// <summary>One steady tone. The simplest signal; the last to fail.</summary>
            SingleTone,
            /// <summary>A ladder across the speech band. The only one that sees frequency.</summary>
            ToneLadder,
            /// <summary>Generated speech. The only one shaped like a voice.</summary>
            Voice,
        }

        /// <summary>How a probe turned out.</summary>
        public enum Outcome
        {
            /// <summary>Not run.</summary>
            NotAttempted,
            /// <summary>Cannot run here — no text-to-speech voice, typically.</summary>
            Unavailable,
            /// <summary>Audio reached the radio.</summary>
            ReachedRadio,
            /// <summary>Audio did not reach the radio.</summary>
            DidNotReach,
        }

        /// <summary>One probe's result, with whatever it noticed.</summary>
        public readonly struct ProbeResult
        {
            public readonly Probe Probe;
            public readonly Outcome Outcome;
            /// <summary>What it read, or why it could not run. Never empty for a real result.</summary>
            public readonly string Detail;

            public ProbeResult(Probe probe, Outcome outcome, string detail)
            {
                Probe = probe;
                Outcome = outcome;
                Detail = detail ?? "";
            }

            public bool Counted => Outcome == Outcome.ReachedRadio || Outcome == Outcome.DidNotReach;
        }

        /// <summary>What the set as a whole supports.</summary>
        public enum Agreement
        {
            /// <summary>Fewer than two probes produced a result.</summary>
            NothingToGoOn,
            /// <summary>Every probe that ran reached the radio.</summary>
            AllReached,
            /// <summary>Every probe that ran failed to reach the radio.</summary>
            NoneReached,
            /// <summary>A majority reached, at least one did not.</summary>
            MostlyReached,
            /// <summary>A majority failed, at least one reached.</summary>
            MostlyFailed,
            /// <summary>Two probes, one each way. No majority exists.</summary>
            EvenlySplit,
        }

        /// <summary>Judge the set.</summary>
        public static Agreement Judge(IReadOnlyList<ProbeResult> results)
        {
            var counted = (results ?? Array.Empty<ProbeResult>()).Where(r => r.Counted).ToList();
            if (counted.Count < 2) return Agreement.NothingToGoOn;

            int reached = counted.Count(r => r.Outcome == Outcome.ReachedRadio);
            int failed = counted.Count - reached;

            if (failed == 0) return Agreement.AllReached;
            if (reached == 0) return Agreement.NoneReached;
            if (reached == failed) return Agreement.EvenlySplit;
            return reached > failed ? Agreement.MostlyReached : Agreement.MostlyFailed;
        }

        /// <summary>
        /// What a disagreement points at, when it points anywhere.
        /// </summary>
        /// <remarks>
        /// Only the first of these is confident, and it is confident because the
        /// mechanism is known rather than guessed: the tone probes bypass the
        /// conditioning chain BY DESIGN (a calibrated reference must arrive
        /// unmodified) and the voice deliberately does not. So tones through and
        /// voice not through is the conditioning chain, or the render, and
        /// nothing else in between.
        /// <para>
        /// The rest are flagged as odd rather than explained. A pattern the
        /// model does not account for is worth reporting exactly as it happened;
        /// inventing a cause for it would be worse than admitting the surprise,
        /// and this text is read by someone deciding what to tell a vendor.
        /// </para>
        /// </remarks>
        public static string ExplainSplit(IReadOnlyList<ProbeResult> results)
        {
            var byProbe = (results ?? Array.Empty<ProbeResult>())
                .Where(r => r.Counted).ToDictionary(r => r.Probe, r => r.Outcome);

            bool Got(Probe p, Outcome o) => byProbe.TryGetValue(p, out Outcome v) && v == o;

            // THE TWO TONE PROBES ARE CHECKED AGAINST EACH OTHER FIRST, and the
            // order is not cosmetic. If they disagree there is no coherent
            // "tones" bloc to hold up against the voice, and treating one as
            // speaking for both produces a confident answer about the wrong
            // comparison. Caught by its own test on 2026-08-25, which is what
            // tests for a decision tree are for.
            if (Got(Probe.SingleTone, Outcome.ReachedRadio) && Got(Probe.ToneLadder, Outcome.DidNotReach))
                return "The single tone reached the radio and the ladder did not. Since the ladder "
                     + "is the same generator at other frequencies, this points at something "
                     + "frequency-dependent — a filter narrower than expected, or a reading taken "
                     + "at a moment the ladder was outside the passband. Read the ladder's own "
                     + "rung-by-rung result before drawing anything from this.";

            if (Got(Probe.ToneLadder, Outcome.ReachedRadio) && Got(Probe.SingleTone, Outcome.DidNotReach))
                return "The ladder reached the radio and the single tone did not, which is odd — "
                     + "the ladder contains tones on both sides of the single tone's frequency. "
                     + "Most likely one of the two runs caught the chain in a different state. "
                     + "Run them again before reading anything into it.";

            // Both tone probes now agree, so they can speak as one.
            bool tonesThrough = Got(Probe.SingleTone, Outcome.ReachedRadio)
                             || Got(Probe.ToneLadder, Outcome.ReachedRadio);
            bool tonesDead = Got(Probe.SingleTone, Outcome.DidNotReach)
                          || Got(Probe.ToneLadder, Outcome.DidNotReach);

            if (tonesThrough && Got(Probe.Voice, Outcome.DidNotReach))
                return "Tones reached the radio and the generated voice did not. The tones bypass "
                     + "the transmit conditioning chain on purpose — a calibrated reference has to "
                     + "arrive unmodified — and the voice deliberately does not. So the difference "
                     + "is in that chain: the noise gate, the noise reduction, or the processing "
                     + "settings. Turning those off and running the voice again would confirm it.";

            if (tonesDead && Got(Probe.Voice, Outcome.ReachedRadio))
                return "The generated voice reached the radio and the tones did not. That is the "
                     + "reverse of what any known mechanism here would produce, so report it as it "
                     + "happened rather than acting on it.";

            return "The probes disagree in a way that does not match a known cause here. The "
                 + "individual results below are the useful part; report them as they are.";
        }

        /// <summary>
        /// The set in words, for the operator.
        /// </summary>
        /// <remarks>
        /// Our own user gets a plain reading and a suggested next step — being
        /// coy with the person using the app helps nobody. The vendor-facing
        /// block is a different document with a different grammar (#217).
        /// </remarks>
        public static string OperatorSummary(IReadOnlyList<ProbeResult> results)
        {
            var all = (results ?? Array.Empty<ProbeResult>()).ToList();
            var counted = all.Where(r => r.Counted).ToList();
            var unavailable = all.Where(r => r.Outcome == Outcome.Unavailable).ToList();

            string skipped = unavailable.Count == 0 ? ""
                : " " + string.Join(" ", unavailable.Select(u =>
                    Name(u.Probe) + " could not run on this computer"
                    + (u.Detail.Length > 0 ? " — " + u.Detail : "") + "."));

            return Judge(all) switch
            {
                Agreement.AllReached =>
                    "All " + counted.Count + " probes reached the radio. Transmit audio is getting "
                    + "through on this path." + skipped,

                Agreement.NoneReached =>
                    "None of the " + counted.Count + " probes reached the radio. Since they fail in "
                    + "different ways and all of them failed, the problem is common to all of them "
                    + "— downstream of where they are injected, not in any one signal." + skipped,

                Agreement.MostlyReached =>
                    "Most probes reached the radio, but not all. " + ExplainSplit(all) + skipped,

                Agreement.MostlyFailed =>
                    "Most probes failed to reach the radio. " + ExplainSplit(all) + skipped,

                Agreement.EvenlySplit =>
                    "Only two probes could run and they disagree, so there is no majority to go on. "
                    + ExplainSplit(all) + skipped,

                _ =>
                    "Fewer than two probes produced a result, so there is nothing to compare."
                    + skipped,
            };
        }

        /// <summary>The probe's name as an operator reads it.</summary>
        public static string Name(Probe p) => p switch
        {
            Probe.SingleTone => "the single tone",
            Probe.ToneLadder => "the tone ladder",
            Probe.Voice => "the generated voice",
            _ => p.ToString(),
        };
    }
}
