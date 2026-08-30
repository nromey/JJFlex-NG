using System;
using System.Collections.Generic;
using System.Text;

namespace Radios.ChainChecks
{
    /// <summary>
    /// The transmit walk as ONE check with two doors.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>#400, and it is #367 applied to the other half.</b> Sprint 41 Track B
    /// joined the RECEIVE doors — one definition, two entry points, each
    /// rendering what suits it and neither owning the words. The transmit side
    /// was never done, which is why the Fixer report of 2026-08-29 said
    /// <i>"Checks read from Receive audio check"</i> and contained no transmit
    /// walk at all, on a run diagnosing a transmit fault.
    /// </para>
    /// <para>
    /// <b>The two doors have opposite problems, and each has what the other
    /// needs.</b> The Audio Workshop's Diagnostics tab can walk all thirteen
    /// stages and <i>cannot transmit</i>, so the three stages that only exist
    /// during a transmission — stage 2, the microphone actually capturing;
    /// stage 11, what the radio says it hears; stage 12, radio frequency out of
    /// the radio — come back as "transmit and run the test again", three times,
    /// every time. The Fixer keys the radio, injects a known tone at
    /// −10 dBFS, holds for eight seconds and replaces the microphone at the
    /// injection point, and <i>did not run the walk</i>. Noel, after five hours
    /// diagnosing Don's radio by hand: <i>"The fix tool's useless for Don's
    /// problem because it doesn't use the audio that is sent when it's doing the
    /// transmit tests to run a TX path check on it."</i>
    /// </para>
    /// <para>
    /// <b>The load-bearing property, and the test of this design: add a rule to
    /// <c>tx-chain-rules.txt</c> and it appears at BOTH doors with no second
    /// edit.</b> That is why this class exists. A second
    /// <c>ChainAnalyzer.Run(RuleSetLoader.TxChain(), …)</c> written beside this
    /// one would compile, run, pass every other test and produce no merge
    /// conflict — two homes for one idea, where one silently falls behind.
    /// A source scan in <c>Radios.Tests</c> refuses it, and a second test
    /// asserts both doors actually CALL this, because a guard that only forbids
    /// a second implementation passes just as happily if the first is never
    /// called. That was precisely the bug.
    /// </para>
    /// <para>
    /// <b>It assembles words; it does not choose them.</b> Every verdict and
    /// every remedy comes from the rule file, every self-written sentence — the
    /// headline, the census, the per-stage line — from
    /// <see cref="ChainAnalyzer"/>. The parts are exposed separately because the
    /// two doors have different containers: the Fixer has a findings list and an
    /// evidence block, the Workshop has one text box and a support-ticket block.
    /// Which PARTS a surface shows is a layout decision; what the parts SAY is
    /// settled here.
    /// </para>
    /// <para>
    /// <b>Never throws.</b> A diagnostic that crashes is worse than one that says
    /// it could not look, and this one runs while a radio is keyed.
    /// </para>
    /// </remarks>
    public static class TransmitChainCheck
    {
        /// <summary>
        /// The id the "nothing could be checked" problem carries. Not a rule id
        /// — no rule produced it, the absence of rules did (#370, the transmit
        /// instance of it).
        /// </summary>
        public const string NothingCheckedId = "tx-nothing-checked";

        /// <summary>
        /// Collect the facts, walk the transmit rules, and phrase the answer. A
        /// null or disconnected radio is an ordinary input: the walk's own first
        /// rule is <c>no-radio</c> and it speaks for it.
        /// </summary>
        /// <param name="rig">The radio, live. Called from inside a keyed window
        /// by the Fixer, and from an idle one by the Workshop — the difference
        /// is a FACT (<c>transmitting</c>) that the rules read, not a mode this
        /// class knows about.</param>
        /// <param name="pcFacts">What only the caller can see: this computer's
        /// microphone, its Windows mute, its levels. The radio layer cannot read
        /// them and never invents them, so a caller that supplies none leaves
        /// the computer's stages honestly unchecked rather than healthy.
        /// <see cref="TxChainFacts.Collect"/> places them ahead of the radio's
        /// own readings, so the evidence block reads along the signal path —
        /// which starts at the microphone.</param>
        public static TransmitCheckResult Run(FlexBase rig,
                                              IEnumerable<DiagnosticFact> pcFacts = null)
        {
            DiagnosticFacts facts;
            try
            {
                facts = TxChainFacts.Collect(rig, pcFacts);
            }
            catch (Exception ex)
            {
                // Collect guards every probe individually, so reaching here
                // means something structural failed. An empty fact set walks to
                // "nothing was checked", which is the honest answer and the one
                // the three-state rule demands.
                JJTrace.Tracing.TraceLine("TransmitChainCheck: collecting the transmit facts "
                                          + "failed — " + ex.Message,
                                          System.Diagnostics.TraceLevel.Error);
                facts = new DiagnosticFacts();
            }

            return From(facts);
        }

        /// <summary>
        /// The walk and the phrasing on their own, for a caller that already
        /// holds the facts — and for tests, which drive every branch an operator
        /// can be shown without a radio and without keying one.
        /// </summary>
        public static TransmitCheckResult From(DiagnosticFacts facts)
            => Using(RuleSetLoader.TxChain(), facts);

        /// <summary>
        /// The same walk against a ruleset the caller supplies. For tests that
        /// need a rule file this build does not ship — above all the empty one,
        /// which is the "we could not check" case and is otherwise reachable
        /// only by damaging the operator's own settings folder.
        /// </summary>
        internal static TransmitCheckResult Using(DiagnosticRuleSet rules, DiagnosticFacts facts)
        {
            facts = facts ?? new DiagnosticFacts();
            ChainReport report = ChainAnalyzer.Run(rules, facts);
            return new TransmitCheckResult(facts, report);
        }
    }

    /// <summary>One thing the transmit walk found, in the rule file's own
    /// words.</summary>
    public sealed class TransmitProblem
    {
        internal TransmitProblem(string id, string whatIsWrong, string whatToDo)
        {
            Id = id ?? "";
            WhatIsWrong = whatIsWrong ?? "";
            WhatToDo = whatToDo ?? "";
        }

        /// <summary>The rule that fired, or
        /// <see cref="TransmitChainCheck.NothingCheckedId"/>. Stable, so a
        /// caller can suppress a problem it has already raised in its own words
        /// without matching on prose.</summary>
        public string Id { get; }

        /// <summary>The rule's verdict, with fact values filled in.</summary>
        public string WhatIsWrong { get; }

        /// <summary>The rule's remedy. Never empty — a problem with nothing to
        /// say about it is not reported as a problem.</summary>
        public string WhatToDo { get; }
    }

    /// <summary>
    /// What one run of the transmit walk found, in the pieces the two doors
    /// need. Every string here is assembled once, so the Fixer's report and the
    /// Workshop's box can never say different things about the same radio.
    /// </summary>
    public sealed class TransmitCheckResult
    {
        internal TransmitCheckResult(DiagnosticFacts facts, ChainReport report)
        {
            Facts = facts;
            Report = report;

            Verdict = Safely(report.Headline, "");
            Census = Safely(report.Census, "");
            Problems = BuildProblems(report);
            Walk = BuildWalk(report);
            Evidence = BuildEvidence(facts, report, Census, Walk);
        }

        /// <summary>Everything that was read, in signal-path order.</summary>
        public DiagnosticFacts Facts { get; }

        /// <summary>The finished walk.</summary>
        public ChainReport Report { get; }

        /// <summary>
        /// The one thing to say out loud: the first dead stage in the operator's
        /// words, or the refusal to call it a clean bill of health while a check
        /// went unmade.
        /// </summary>
        public string Verdict { get; }

        /// <summary>How much of the chain was actually seen.</summary>
        public string Census { get; }

        /// <summary>Everything the walk found, in walk order.</summary>
        public IReadOnlyList<TransmitProblem> Problems { get; }

        /// <summary>
        /// The verdict, unless a problem already says those exact words — in
        /// which case, empty.
        /// </summary>
        /// <remarks>
        /// <para>
        /// For a surface that renders <see cref="Problems"/> as its own list.
        /// The headline IS the first broken stage's verdict and remedy, so a
        /// findings surface showing both says the same thing twice; a surface
        /// with one text box shows <see cref="Verdict"/> and no list, and never
        /// has the choice.
        /// </para>
        /// <para>
        /// It is not simply "empty when something is broken". The two sentences
        /// that matter most are the ones no rule produced: "nothing is wrong"
        /// and "this is not a clean bill of health, because a check went
        /// unmade". Those have no problem to carry them and must be said.
        /// </para>
        /// </remarks>
        public string VerdictNotCarriedByProblems
            => Report.FirstBroken != null || NothingCouldBeChecked ? "" : Verdict;

        /// <summary>The stage-by-stage walk, one prose line each.</summary>
        public string Walk { get; }

        /// <summary>
        /// The readings behind the answer, in signal-path order, with the census
        /// and the walk above them. This is what goes into the Fixer's report —
        /// the document that reaches the radio's manufacturer.
        /// </summary>
        public string Evidence { get; }

        /// <summary>
        /// The full support-ticket block: the operator's reproduction claim, the
        /// radio's identity, the software's, every reading, the walk, and our
        /// reading of it labelled as ours and last (#217).
        /// </summary>
        /// <remarks>
        /// A method rather than a property because only a caller holding the rig
        /// and the build can supply the identity lines, and
        /// <see cref="ChainReport.EvidenceText"/> already owns the layout.
        /// </remarks>
        public string EvidenceForSupport(IEnumerable<string> station = null,
                                         IEnumerable<string> build = null,
                                         string reproduction = null)
        {
            try { return Report.EvidenceText(station, build, reproduction); }
            catch (Exception ex)
            {
                JJTrace.Tracing.TraceLine("TransmitChainCheck: the support block failed — "
                                          + ex.Message, System.Diagnostics.TraceLevel.Warning);
                // Half an evidence block is still worth sending; an exception is
                // not.
                try { return Report.EvidenceText(); }
                catch { return Evidence; }
            }
        }

        /// <summary>
        /// True when nothing at all was checked — a missing embedded rule file,
        /// an unreadable override, or an override that is empty or all comments.
        /// Reported as its own problem rather than being allowed to read as good
        /// news (#370).
        /// </summary>
        public bool NothingCouldBeChecked
            => Report.ChecksMade == 0 && Report.StagesHealthy == 0;

        private static IReadOnlyList<TransmitProblem> BuildProblems(ChainReport report)
        {
            var list = new List<TransmitProblem>();

            foreach (StageResult s in report.Stages)
            {
                if (s.Verdict != StageVerdict.Broken) continue;
                string wrong = s.Message.Length != 0
                    ? s.Message
                    : "Something is wrong at " + s.Title + ".";
                // A rule may legitimately offer no remedy; a finding must still
                // say something, so the honest fallback names the gap rather
                // than inventing advice.
                string todo = s.Remedy.Length != 0
                    ? s.Remedy
                    : "The check that found this offers no remedy, so there is nothing here to "
                      + "act on. Include this in your report.";
                list.Add(new TransmitProblem(s.Rule?.Id ?? s.Stage.Name, wrong, todo));
            }

            // NOTHING WAS CHECKED IS A THIRD ANSWER, and collapsing it into
            // "nothing is wrong" is the worst available collapse (#370). It
            // travels as a problem so every caller carries it without having to
            // know it exists.
            if (report.ChecksMade == 0 && report.StagesHealthy == 0)
            {
                string why = report.RuleProblems.Count != 0
                    ? report.RuleProblems[0]
                    : "Nothing here can restore them.";
                list.Add(new TransmitProblem(TransmitChainCheck.NothingCheckedId,
                    report.Headline(),
                    "Quote your test ID when you report this. " + why));
            }

            return list;
        }

        private static string BuildWalk(ChainReport report)
        {
            var sb = new StringBuilder();
            foreach (StageResult s in report.Stages) sb.AppendLine(s.Line());
            foreach (string p in report.RuleProblems) sb.AppendLine(p);
            return sb.ToString().TrimEnd();
        }

        private static string BuildEvidence(DiagnosticFacts facts, ChainReport report,
                                            string census, string walk)
        {
            // Same shape as the receive walk's evidence block, deliberately: the
            // two now sit in one report and a reader walks straight from one
            // into the other.
            var sb = new StringBuilder();
            sb.AppendLine("Transmit chain, walked from your microphone to the antenna");
            sb.AppendLine("-----------------------------------------------------------");
            if (census.Length != 0) sb.AppendLine(census);
            sb.AppendLine();
            sb.AppendLine("Stage by stage:");
            if (walk.Length != 0) sb.AppendLine(walk);
            sb.AppendLine();
            sb.AppendLine("Readings, in signal-path order:");
            if (facts.All.Count == 0)
            {
                sb.AppendLine("Nothing was collected.");
            }
            else
            {
                foreach (DiagnosticFact f in facts.All) sb.AppendLine(f.EvidenceLine());
            }
            sb.AppendLine();
            // Describe() ends in its own full stop; a second one is an audible
            // stumble for a screen reader, which is who reads this.
            sb.AppendLine("Checks read from "
                          + StageResult.Sentence(report.Rules?.Describe() ?? "nowhere"));
            return sb.ToString();
        }

        private static string Safely(Func<string> f, string fallback)
        {
            try { return f() ?? fallback; }
            catch (Exception ex)
            {
                JJTrace.Tracing.TraceLine("TransmitChainCheck: phrasing failed — " + ex.Message,
                                          System.Diagnostics.TraceLevel.Warning);
                return fallback;
            }
        }
    }
}
