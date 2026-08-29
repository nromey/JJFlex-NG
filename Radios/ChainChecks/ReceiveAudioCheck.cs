using System;
using System.Collections.Generic;
using System.Text;

namespace Radios.ChainChecks
{
    /// <summary>
    /// The receive walk as ONE check with two doors.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One definition, two entry points — Noel's ruling, 2026-08-28 (#367):</b>
    /// <i>"if someone has to add a test, it needs to end up on the same test if
    /// that makes sense, so as stage 0, it does rx audio as well, but you can
    /// just go to the other submenu option if you just wanted to test rx
    /// audio."</i>
    /// </para>
    /// <para>
    /// So the Fixer's stage 0 runs the receive checks as part of the chain — the
    /// report that reaches a manufacturer is complete without the operator
    /// having had to know there was a second thing to run — and the Audio
    /// Workshop's "Test my receive audio" button reaches the same check
    /// directly, for an operator who wants only that answer.
    /// </para>
    /// <para>
    /// <b>The load-bearing property, and the test of this design: add a rule to
    /// <c>rx-chain-rules.txt</c> and it appears at BOTH doors with no second
    /// edit.</b> That is why this class exists at all. Two stage sets, or a
    /// second phrasing beside this one, would be the project's dominant defect
    /// wearing a diagnostic's clothes — two homes for one idea, where one
    /// silently falls behind.
    /// </para>
    /// <para>
    /// <b>It assembles words; it does not choose them.</b> Every verdict and
    /// every remedy comes from the rule file, every measurement sentence from
    /// <see cref="RxChainFacts"/>, and every self-written sentence — the
    /// headline, the census, the per-stage line — from
    /// <see cref="ChainAnalyzer"/>. The parts are exposed separately because
    /// the two doors have different containers: the Fixer has a findings list
    /// and an evidence block, the Workshop has one text box. Which PARTS a
    /// surface shows is a layout decision; what the parts SAY is settled here.
    /// </para>
    /// <para>
    /// <b>Never throws.</b> A diagnostic that crashes is worse than one that
    /// says it could not look, and this one runs when something is already
    /// wrong.
    /// </para>
    /// </remarks>
    public static class ReceiveAudioCheck
    {
        /// <summary>
        /// The id the "nothing could be checked" problem carries. Not a rule id
        /// — no rule produced it, the absence of rules did.
        /// </summary>
        public const string NothingCheckedId = "rx-nothing-checked";

        /// <summary>
        /// Collect the facts from the radio, walk the receive rules, and phrase
        /// the answer. A null or disconnected radio is an ordinary input:
        /// <see cref="RxChainFacts"/> records why each fact is absent and the
        /// <c>rx-no-radio</c> rule speaks for it.
        /// </summary>
        public static ReceiveCheckResult Run(FlexBase rig)
        {
            DiagnosticFacts facts;
            try
            {
                facts = RxChainFacts.Collect(rig);
            }
            catch (Exception ex)
            {
                // Collect guards every probe individually, so reaching here
                // means something structural failed. An empty fact set walks to
                // "nothing was checked", which is the honest answer and the one
                // the three-state rule demands.
                JJTrace.Tracing.TraceLine("ReceiveAudioCheck: collecting the receive facts failed — "
                                          + ex.Message, System.Diagnostics.TraceLevel.Error);
                facts = new DiagnosticFacts();
            }

            return From(facts);
        }

        /// <summary>
        /// The walk and the phrasing on their own, for a caller that already
        /// holds the facts — and for tests, which drive every branch an
        /// operator can be shown without a radio.
        /// </summary>
        public static ReceiveCheckResult From(DiagnosticFacts facts)
            => Using(RuleSetLoader.RxChain(), facts);

        /// <summary>
        /// The same walk against a ruleset the caller supplies. For tests that
        /// need a rule file this build does not ship — above all the empty one,
        /// which is the "we could not check" case (#370) and is otherwise
        /// reachable only by damaging the operator's own settings folder.
        /// </summary>
        internal static ReceiveCheckResult Using(DiagnosticRuleSet rules, DiagnosticFacts facts)
        {
            facts = facts ?? new DiagnosticFacts();
            ChainReport report = ChainAnalyzer.Run(rules, facts);
            return new ReceiveCheckResult(facts, report);
        }
    }

    /// <summary>One thing the receive walk found, in the rule file's own
    /// words.</summary>
    public sealed class ReceiveProblem
    {
        internal ReceiveProblem(string id, string whatIsWrong, string whatToDo)
        {
            Id = id ?? "";
            WhatIsWrong = whatIsWrong ?? "";
            WhatToDo = whatToDo ?? "";
        }

        /// <summary>The rule that fired, or
        /// <see cref="ReceiveAudioCheck.NothingCheckedId"/>. Stable, so a
        /// caller can suppress a problem it has already raised in its own
        /// words without matching on prose.</summary>
        public string Id { get; }

        /// <summary>The rule's verdict, with fact values filled in.</summary>
        public string WhatIsWrong { get; }

        /// <summary>The rule's remedy. Never empty — a problem with nothing to
        /// say about it is not reported as a problem.</summary>
        public string WhatToDo { get; }
    }

    /// <summary>
    /// What one run of the receive check found, in the pieces the two doors
    /// need. Every string here is assembled once, so the Fixer's report and the
    /// Workshop's box can never say different things about the same radio.
    /// </summary>
    public sealed class ReceiveCheckResult
    {
        internal ReceiveCheckResult(DiagnosticFacts facts, ChainReport report)
        {
            Facts = facts;
            Report = report;

            Verdict = Safely(report.Headline, "");
            Census = Safely(report.Census, "");
            Arrival = Safely(() => RxChainFacts.ArrivalSentence(facts), "");
            Problems = BuildProblems(report);
            Walk = BuildWalk(report);
            Evidence = BuildEvidence(facts, report, Census, Walk);
        }

        /// <summary>Everything that was read, in signal-path order.</summary>
        public DiagnosticFacts Facts { get; }

        /// <summary>The finished walk.</summary>
        public ChainReport Report { get; }

        /// <summary>
        /// The one thing to say out loud: the first dead stage in the
        /// operator's words, or the refusal to call it a clean bill of health
        /// while a check went unmade.
        /// </summary>
        public string Verdict { get; }

        /// <summary>How much of the chain was actually seen.</summary>
        public string Census { get; }

        /// <summary>
        /// The measurement, said plainly: how much audio has actually been
        /// arriving from the radio, over what window, and whether zero is the
        /// expected answer for this setup. Empty when nothing was measured at
        /// all.
        /// </summary>
        /// <remarks>
        /// This is the half a manufacturer's support desk asks about first
        /// (#350), and the half that survives a reader who distrusts our
        /// software (#217): every other fact in this walk is a switch WE set.
        /// </remarks>
        public string Arrival { get; }

        /// <summary>
        /// Verdict and measurement as one paragraph, for a surface that shows a
        /// single run of prose. Surfaces with room to breathe use the two parts
        /// separately.
        /// </summary>
        public string Answer => Arrival.Length == 0
            ? Verdict
            : (Verdict.Length == 0 ? Arrival : Verdict + " " + Arrival);

        /// <summary>Everything the walk found, in walk order.</summary>
        public IReadOnlyList<ReceiveProblem> Problems { get; }

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
        /// The readings behind the answer, in signal-path order, with the
        /// census and the walk above them. This is what goes into the Fixer's
        /// report — the document that reaches the radio's manufacturer.
        /// </summary>
        public string Evidence { get; }

        /// <summary>
        /// True when nothing at all was checked — a missing embedded rule file,
        /// an unreadable override, or an override that is empty or all
        /// comments. Reported as its own problem rather than being allowed to
        /// read as good news (#370).
        /// </summary>
        public bool NothingCouldBeChecked
            => Report.ChecksMade == 0 && Report.StagesHealthy == 0;

        private static IReadOnlyList<ReceiveProblem> BuildProblems(ChainReport report)
        {
            var list = new List<ReceiveProblem>();

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
                list.Add(new ReceiveProblem(s.Rule?.Id ?? s.Stage.Name, wrong, todo));
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
                list.Add(new ReceiveProblem(ReceiveAudioCheck.NothingCheckedId,
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
            // Same shape as AudioSetupCheck's evidence block — a titled section,
            // then one fact per line — because the two now sit inside the same
            // stage's record and a reader walks straight from one into the
            // other.
            var sb = new StringBuilder();
            sb.AppendLine("Receive audio, walked from the radio's outputs to what arrived here");
            sb.AppendLine("--------------------------------------------------------------------");
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
                JJTrace.Tracing.TraceLine("ReceiveAudioCheck: phrasing failed — " + ex.Message,
                                          System.Diagnostics.TraceLevel.Warning);
                return fallback;
            }
        }
    }
}
