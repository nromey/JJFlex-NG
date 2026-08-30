using System;
using System.Collections.Generic;
using System.Text;

namespace Radios.ChainChecks
{
    /// <summary>
    /// The words a finished chain walk turns into, wherever it points.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Written because the integration pass caught this track red-handed.</b>
    /// <see cref="TransmitChainCheck"/> was shaped on
    /// <see cref="ReceiveAudioCheck"/>, deliberately and correctly — one
    /// definition, two doors, both halves the same shape — and three pieces of
    /// it came across as copies: the stage-by-stage walk, the sentence for a
    /// rule that offers no remedy, and the sentence for a rule file that loaded
    /// nothing. Every copy was right on the day it was written. That is the
    /// point: two homes for one idea produce no conflict, no build error and no
    /// failing test, right up until one of them is corrected.
    /// </para>
    /// <para>
    /// <b>The receive walk and the transmit walk are one idea pointed in two
    /// directions</b>, and the analyzer already knows which direction it is
    /// running — the chain names itself. So the phrasing has one home and the
    /// two checks differ only in what a caller is handed back.
    /// </para>
    /// <para>
    /// Internal: these are assembly-private sentences, reachable only through
    /// the two checks. Nothing outside gets to build a walk of its own.
    /// </para>
    /// </remarks>
    internal static class ChainWalkPhrasing
    {
        /// <summary>
        /// What a finding says when the rule that produced it offers no remedy.
        /// </summary>
        /// <remarks>
        /// A rule may legitimately have nothing to advise, but a finding must
        /// still say something — so this names the gap rather than inventing
        /// advice, and sends the reading somewhere it can still do good.
        /// </remarks>
        public const string NoRemedy =
            "The check that found this offers no remedy, so there is nothing here to "
            + "act on. Include this in your report.";

        /// <summary>The stage-by-stage walk, one prose line each, with any
        /// lines of the rule file this build could not read after it.</summary>
        public static string Walk(ChainReport report)
        {
            var sb = new StringBuilder();
            foreach (StageResult s in report.Stages) sb.AppendLine(s.Line());
            foreach (string p in report.RuleProblems) sb.AppendLine(p);
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Every broken stage as a problem, in walk order, plus the third answer
        /// when there was nothing to check at all.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>"Nothing is wrong", "something is wrong" and "we could not check"
        /// are three different answers</b>, and collapsing the third into the
        /// first is the worst available collapse (#370). It travels as a problem
        /// like any other so every caller carries it without having to know it
        /// exists.
        /// </para>
        /// <para>
        /// Generic over the carrier, because the two checks hand back different
        /// types and the ALGORITHM is what must not be written twice. One walk
        /// order, one set of words, two envelopes.
        /// </para>
        /// </remarks>
        /// <param name="nothingCheckedId">The id the third answer carries. Not a
        /// rule id — no rule produced it, the absence of rules did.</param>
        /// <param name="make">Builds one problem from its id, what is wrong, and
        /// what to do about it.</param>
        public static IReadOnlyList<T> Problems<T>(ChainReport report, string nothingCheckedId,
                                                   Func<string, string, string, T> make)
        {
            var list = new List<T>();

            foreach (StageResult s in report.Stages)
            {
                if (s.Verdict != StageVerdict.Broken) continue;
                string wrong = s.Message.Length != 0
                    ? s.Message
                    : "Something is wrong at " + s.Title + ".";
                string todo = s.Remedy.Length != 0 ? s.Remedy : NoRemedy;
                list.Add(make(s.Rule?.Id ?? s.Stage.Name, wrong, todo));
            }

            if (report.ChecksMade == 0 && report.StagesHealthy == 0)
            {
                string why = report.RuleProblems.Count != 0
                    ? report.RuleProblems[0]
                    : "Nothing here can restore them.";
                list.Add(make(nothingCheckedId, report.Headline(),
                              "Quote your test ID when you report this. " + why));
            }

            return list;
        }

        /// <summary>
        /// The readings behind the answer, in signal-path order, with the census
        /// and the walk above them. This is what goes into the Fixer's report —
        /// the document that reaches the radio's manufacturer.
        /// </summary>
        /// <param name="title">What this block is a walk OF, in the operator's
        /// words. The one line the two chains do not share, because it is the
        /// one thing about them that genuinely differs.</param>
        public static string Evidence(DiagnosticFacts facts, ChainReport report,
                                      string census, string walk, string title)
        {
            var sb = new StringBuilder();
            sb.AppendLine(title);
            sb.AppendLine(new string('-', Math.Max(title.Length, 1)));
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

        /// <summary>
        /// A sentence the walk produced, or a fallback — never an exception. A
        /// diagnostic that crashes while phrasing its own answer is worse than
        /// one that says less.
        /// </summary>
        public static string Safely(Func<string> f, string fallback, string who)
        {
            try { return f() ?? fallback; }
            catch (Exception ex)
            {
                JJTrace.Tracing.TraceLine(who + ": phrasing failed — " + ex.Message,
                                          System.Diagnostics.TraceLevel.Warning);
                return fallback;
            }
        }
    }
}
