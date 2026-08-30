using System;
using System.Collections.Generic;

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
            Walk = ChainWalkPhrasing.Walk(report);
            Evidence = ChainWalkPhrasing.Evidence(
                facts, report, Census, Walk,
                "Receive audio, walked from the radio's outputs to what arrived here");
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

        // THE WALK'S OWN WORDS LIVE IN ChainWalkPhrasing, not here. The receive
        // walk and the transmit walk are one idea pointed in two directions —
        // same stage order, same three-state rule, same sentence for a rule
        // with no remedy — and the integration pass caught the transmit half
        // copying these bodies out of this file on 2026-08-29. What is left
        // here is the ENVELOPE: which type a caller is handed, and what this
        // chain's evidence block is called.

        private static IReadOnlyList<ReceiveProblem> BuildProblems(ChainReport report)
            => ChainWalkPhrasing.Problems(report, ReceiveAudioCheck.NothingCheckedId,
                                          (id, wrong, todo) => new ReceiveProblem(id, wrong, todo));

        private static string Safely(Func<string> f, string fallback)
            => ChainWalkPhrasing.Safely(f, fallback, "ReceiveAudioCheck");
    }
}
