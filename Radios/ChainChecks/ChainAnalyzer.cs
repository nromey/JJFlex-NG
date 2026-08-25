using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Radios.ChainChecks
{
    /// <summary>
    /// What one stage of the chain came out as.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The three that matter are <see cref="Broken"/>, <see cref="Healthy"/> and
    /// <see cref="NotObservable"/>. Saying "all good" when part of the path could
    /// not be seen sends the operator hunting the wrong end of their station,
    /// which is worse than saying nothing at all.
    /// </para>
    /// <para>
    /// <see cref="NotInPath"/> is a fourth answer and a deliberate addition, not
    /// a softening of the rule. An operator speaking into a microphone plugged
    /// into the radio's own front panel has no computer audio, no Opus encoder
    /// and no packets: reporting four stages as "could not check" would be
    /// literally true and practically a lie, because it invites them to go
    /// looking for a problem in a path they are not using. Unreadable means we
    /// tried and failed; not-in-path means there was nothing there to try. They
    /// are counted separately and never added together.
    /// </para>
    /// </remarks>
    public enum StageVerdict
    {
        /// <summary>A rule fired. Something here is wrong.</summary>
        Broken,

        /// <summary>Every check that applied was made, and none of them fired.</summary>
        Healthy,

        /// <summary>We could not see this stage from this computer.</summary>
        NotObservable,

        /// <summary>This stage is not part of the operator's transmit path.</summary>
        NotInPath
    }

    /// <summary>One stage's answer, with everything needed to justify it.</summary>
    public sealed class StageResult
    {
        internal StageResult(DiagnosticStage stage) { Stage = stage; }

        /// <summary>The stage this is about.</summary>
        public DiagnosticStage Stage { get; }

        /// <summary>The answer.</summary>
        public StageVerdict Verdict { get; internal set; }

        /// <summary>The rule that fired, when the stage is broken.</summary>
        public DiagnosticRule Rule { get; internal set; }

        /// <summary>What is wrong, in the operator's words, with the fact values
        /// already substituted in. Empty unless this stage is broken.</summary>
        public string Message { get; internal set; } = "";

        /// <summary>What to do about it, with fact values substituted. May be
        /// empty when a rule offers no remedy.</summary>
        public string Remedy { get; internal set; } = "";

        /// <summary>Why this stage could not be checked, or why it is not in the
        /// path. One entry per distinct reason, in the operator's words.</summary>
        public List<string> Reasons { get; } = new List<string>();

        /// <summary>Checks that were actually made here.</summary>
        public int ChecksMade { get; internal set; }

        /// <summary>Checks that applied here and could not be made.</summary>
        public int ChecksUnreadable { get; internal set; }

        /// <summary>
        /// True when this stage came back unobservable only because nothing
        /// applied YET — almost always "transmit and run the check again".
        /// </summary>
        /// <remarks>
        /// Both this and a permanently unobservable stage are
        /// <see cref="StageVerdict.NotObservable"/>, because both mean "we did
        /// not look here" and neither may ever read as healthy. They are told
        /// apart only in the wording, and that is worth doing: "four stages need
        /// you to be transmitting" is an instruction, while "seven stages cannot
        /// be seen from this computer" is a dead end, and reading the first as
        /// the second makes the report sound more broken than the radio is.
        /// </remarks>
        public bool MeasurableLater { get; internal set; }

        /// <summary>The readings behind this stage's answer, for the evidence
        /// block.</summary>
        public List<DiagnosticFact> Evidence { get; } = new List<DiagnosticFact>();

        /// <summary>Stage number and name, the way every sentence about it
        /// starts: "stage 9, mic profile on the radio".</summary>
        public string Title =>
            "stage " + Stage.Number.ToString(CultureInfo.CurrentCulture) + ", " + Stage.Name;

        /// <summary>
        /// One sentence for the stage-by-stage list. Prose, because it is read
        /// aloud — never a status word in a column.
        /// </summary>
        public string Line()
        {
            switch (Verdict)
            {
                case StageVerdict.Broken:
                    return Capitalize(Title) + ": this is where it stops. " + Message;

                case StageVerdict.NotInPath:
                    return Sentence(Capitalize(Title) + ": not part of your transmit path"
                         + (Reasons.Count != 0 ? ", " + Reasons[0] : ""));

                case StageVerdict.NotObservable:
                    return Sentence(Capitalize(Title)
                         + (MeasurableLater ? ": nothing to check right now" : ": could not be checked")
                         + (Reasons.Count != 0 ? " — " + string.Join("; ", Reasons) : ""));

                default:
                    string s = Capitalize(Title) + ": checked, nothing wrong.";
                    if (ChecksUnreadable > 0)
                    {
                        s += " " + Plural(ChecksUnreadable, "One further check", ChecksUnreadable + " further checks")
                           + " here could not be made"
                           + (Reasons.Count != 0 ? " — " + string.Join("; ", Reasons) : "");
                        s = Sentence(s);
                    }
                    return s;
            }
        }

        private static string Plural(int n, string one, string many) => n == 1 ? one : many;

        /// <summary>
        /// Finish a sentence without doubling its full stop. Rule-file text is
        /// written as prose and usually ends in one already; two in a row is a
        /// stumble for a reader and an audible one for a screen reader.
        /// </summary>
        internal static string Sentence(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            char last = s[s.Length - 1];
            return last == '.' || last == '!' || last == '?' ? s : s + ".";
        }

        private static string Capitalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return char.ToUpper(s[0], CultureInfo.CurrentCulture) + s.Substring(1);
        }
    }

    /// <summary>
    /// The finished walk: what is wrong, how much of the path we could actually
    /// see, and the readings that justify both.
    /// </summary>
    public sealed class ChainReport
    {
        internal ChainReport(DiagnosticRuleSet rules, DiagnosticFacts facts)
        {
            Rules = rules;
            Facts = facts;
            At = facts?.CollectedAt ?? DateTime.Now;
        }

        /// <summary>The ruleset that produced this.</summary>
        public DiagnosticRuleSet Rules { get; }

        /// <summary>Everything that was read, in signal-path order.</summary>
        public DiagnosticFacts Facts { get; }

        /// <summary>The moment this describes, local time.</summary>
        public DateTime At { get; }

        /// <summary>Every stage, in walk order.</summary>
        public List<StageResult> Stages { get; } = new List<StageResult>();

        /// <summary>The first stage in walk order that is broken, or null.
        /// This is the answer: a chain fails at its earliest break, and
        /// everything after it is a consequence rather than a second fault.</summary>
        public StageResult FirstBroken { get; internal set; }

        /// <summary>Checks that applied and were made.</summary>
        public int ChecksMade { get; internal set; }

        /// <summary>Checks that applied and could not be made.</summary>
        public int ChecksUnreadable { get; internal set; }

        /// <summary>Checks that applied at all: made plus unreadable.</summary>
        public int ChecksApplicable => ChecksMade + ChecksUnreadable;

        /// <summary>Extra lines this build could not read out of the rule file.
        /// Never hidden — a ruleset that silently lost rules would report a
        /// healthy radio with total confidence.</summary>
        public IReadOnlyList<string> RuleProblems => Rules?.Problems ?? (IReadOnlyList<string>)Array.Empty<string>();

        private int CountOf(StageVerdict v)
        {
            int n = 0;
            foreach (StageResult s in Stages) if (s.Verdict == v) n++;
            return n;
        }

        /// <summary>Stages checked clean.</summary>
        public int StagesHealthy => CountOf(StageVerdict.Healthy);

        /// <summary>Stages we could not see, for any reason.</summary>
        public int StagesUnobservable => CountOf(StageVerdict.NotObservable);

        /// <summary>Stages that had nothing to check yet but would have
        /// something in other circumstances — usually while transmitting.</summary>
        public int StagesPending
        {
            get
            {
                int n = 0;
                foreach (StageResult s in Stages)
                    if (s.Verdict == StageVerdict.NotObservable && s.MeasurableLater) n++;
                return n;
            }
        }

        /// <summary>
        /// Stages that came back unobservable for a reason other than "nothing
        /// applied yet": either the rule file declares them permanently
        /// unobservable, or their checks could not be read this time.
        /// </summary>
        /// <remarks>
        /// Those two are NOT the same thing, and the report deliberately does
        /// not pretend to tell them apart in a count. It says "could not be
        /// checked" and lets each stage give its own reason, because the honest
        /// distinction — a standing limitation of the app versus a radio that
        /// happens to be unplugged — lives in the reason, not in the tally.
        /// </remarks>
        public int StagesBlind => StagesUnobservable - StagesPending;

        /// <summary>Stages that are not part of this operator's path.</summary>
        public int StagesNotInPath => CountOf(StageVerdict.NotInPath);

        /// <summary>Stages with something wrong.</summary>
        public int StagesBroken => CountOf(StageVerdict.Broken);

        /// <summary>
        /// The one thing to say out loud. Names the first dead stage in the
        /// operator's words and what to do about it — or, when nothing fired,
        /// refuses to call it a clean bill of health while any check went
        /// unmade.
        /// </summary>
        public string Headline()
        {
            if (FirstBroken != null)
            {
                string s = FirstBroken.Message.Length != 0
                    ? FirstBroken.Message
                    : "Something is wrong at " + FirstBroken.Title + ".";
                return FirstBroken.Remedy.Length == 0 ? s : s + " " + FirstBroken.Remedy;
            }

            // NOTHING WAS CHECKED AT ALL. This has to be said before anything
            // else, because every counter a clean bill of health depends on is
            // also zero here and the sentence below would read as "all is
            // well".
            //
            // It is reachable in ordinary use: the rules live in a file, and a
            // missing embedded copy, an unreadable override, or an override
            // that is empty or all comments all produce a ruleset with no
            // stages. Census() already says so; this is the line that gets
            // SPOKEN and sits at the top of the report, so it has to say so
            // too.
            if (ChecksMade == 0 && StagesHealthy == 0)
            {
                string s = "Nothing was checked, so nothing can be said about your transmit chain. "
                         + "The checks themselves could not be loaded";
                if (RuleProblems.Count != 0) s += ": " + RuleProblems[0];
                else s += ".";
                return s;
            }

            if (ChecksUnreadable > 0 || StagesUnobservable > 0)
            {
                return "Nothing that could be checked came back wrong, but "
                     + UncheckedPhrase()
                     + " — so this is not a clean bill of health. "
                     + "If you are still not being heard, the problem is most likely in one of those.";
            }

            return "Every stage of your transmit chain that applies to your setup was checked, and nothing is wrong.";
        }

        private string UncheckedPhrase()
        {
            var parts = new List<string>();
            if (ChecksUnreadable > 0)
            {
                parts.Add(ChecksUnreadable == 1
                    ? "one check could not be made"
                    : ChecksUnreadable + " checks could not be made");
            }
            if (StagesPending > 0)
            {
                parts.Add(StagesPending == 1
                    ? "one stage had nothing to check yet"
                    : StagesPending + " stages had nothing to check yet");
            }
            if (StagesBlind > 0)
            {
                // "could not be checked", NOT "cannot be seen from this
                // computer". This bucket holds two different things — the
                // stages the rule file declares permanently unobservable, and
                // stages whose checks were merely unreadable this time, which
                // with no radio connected is most of them. Only the first kind
                // is a standing limitation, and claiming the second kind is one
                // would send an operator looking for a hole in the app when the
                // truth is that nothing was plugged in. Each stage's own line
                // gives its actual reason, verbatim.
                parts.Add(StagesBlind == 1
                    ? "one stage could not be checked"
                    : StagesBlind + " stages could not be checked");
            }

            if (parts.Count == 0) return "some of it went unchecked";
            if (parts.Count == 1) return parts[0];
            return string.Join(", ", parts.GetRange(0, parts.Count - 1))
                 + " and " + parts[parts.Count - 1];
        }

        /// <summary>
        /// How much of the chain we actually saw, said plainly. This sentence is
        /// the whole point of the three-state rule: "checked 14 of 19" is honest,
        /// "all good" when five went unread is not.
        /// </summary>
        public string Census()
        {
            var sb = new StringBuilder();

            if (ChecksApplicable == 0)
            {
                sb.Append("No check applied to your setup, so nothing was tested.");
            }
            else
            {
                sb.Append("Checked ").Append(ChecksMade).Append(" of ")
                  .Append(ChecksApplicable).Append(ChecksApplicable == 1 ? " check" : " checks");
                if (ChecksUnreadable > 0)
                    sb.Append("; ").Append(ChecksUnreadable).Append(" could not be made");
                sb.Append('.');
            }

            int checkedStages = StagesHealthy + StagesBroken;
            sb.Append(" Of ").Append(Stages.Count).Append(" stages, ").Append(checkedStages)
              .Append(checkedStages == 1 ? " was checked" : " were checked");
            if (StagesPending > 0)
                sb.Append(", ").Append(StagesPending).Append(" had nothing to check yet");
            if (StagesBlind > 0)
                sb.Append(", ").Append(StagesBlind).Append(" could not be checked");
            if (StagesNotInPath > 0)
                sb.Append(", ").Append(StagesNotInPath).Append(" are not in your transmit path");
            sb.Append('.');

            return sb.ToString();
        }

        /// <summary>
        /// The whole report as text: the answer first, then the honest census,
        /// then the walk. Lines and bullets only — no columns, no table, because
        /// this is written to be read aloud.
        /// </summary>
        public string ToText()
        {
            var sb = new StringBuilder();
            sb.Append(Rules?.Title ?? "Chain check").Append(", ")
              .Append(At.ToString("d MMMM yyyy, HH:mm:ss", CultureInfo.CurrentCulture))
              .AppendLine(".");
            sb.AppendLine();

            sb.AppendLine(Headline());
            sb.AppendLine();
            sb.AppendLine(Census());
            sb.AppendLine();

            sb.AppendLine("Stage by stage:");
            foreach (StageResult s in Stages)
                sb.Append("  ").AppendLine(s.Line());

            if (RuleProblems.Count != 0)
            {
                sb.AppendLine();
                sb.AppendLine("The rule file has " + RuleProblems.Count
                    + (RuleProblems.Count == 1 ? " line" : " lines")
                    + " this build could not read, so some checks may be missing:");
                foreach (string p in RuleProblems) sb.Append("  ").AppendLine(p);
            }

            return sb.ToString();
        }

        /// <summary>
        /// The copyable evidence block: every reading behind the verdict, with
        /// units, ages and where each came from, plus what was running and which
        /// rules judged it. Written so it can be pasted straight into an email to
        /// Flex support with nothing translated first.
        /// </summary>
        /// <param name="station">Lines identifying the radio and the connection —
        /// model, serial, firmware, how it is connected. Supplied by the caller
        /// because only the radio layer knows them.</param>
        /// <param name="build">Lines identifying what software was running.
        /// Supplied by the caller, which reads them from
        /// <see cref="DiagnosticSnapshot"/> — nothing here assembles its own
        /// version strings.</param>
        /// <param name="reproduction">
        /// The operator's own statement that the fault also happens outside JJ
        /// Flexible — typically in SmartSDR. Goes FIRST, because it is the
        /// sentence that reframes the whole document from a third-party
        /// complaint into a question about the radio.
        /// <para>
        /// We can never write this ourselves and must never imply it. Only the
        /// operator knows, and an unverifiable claim in our voice would poison
        /// the one section a vendor is most likely to act on. Empty means the
        /// section is omitted entirely rather than hedged.
        /// </para>
        /// </param>
        public string EvidenceText(IEnumerable<string> station = null, IEnumerable<string> build = null,
                                   string reproduction = null)
        {
            var sb = new StringBuilder();

            sb.Append(Rules?.Title ?? "Chain check").AppendLine(" — measurements");
            sb.Append("Taken ").Append(At.ToString("d MMMM yyyy, HH:mm:ss", CultureInfo.CurrentCulture))
              .Append(' ').Append(TimeZoneInfo.Local.StandardName).AppendLine(".");
            sb.AppendLine();

            // ── ORDER RULED BY NOEL, 2026-08-25 ────────────────────────────
            //
            // "They won't wanna touch JJ Flexible with a ten foot pole — that's
            // us that does that. But they will care if we say we tried it in
            // SmartSDR, here are the goods you can use to tell where fault
            // happens."
            //
            // This block used to lead with OUR verdict, OUR census and the rule
            // id that produced them. That is a well-made bug report FROM A
            // THIRD-PARTY APPLICATION, and the predictable reply from any
            // vendor's support desk is "that is third-party software, please
            // reproduce in SmartSDR" — at which point the operator has spent
            // their credibility and still has to do the work.
            //
            // The litmus test for every line here: WOULD THIS STILL BE USEFUL
            // TO SOMEONE WHO DISTRUSTS OUR SOFTWARE COMPLETELY? The radio's own
            // identity passes. Named readings with timestamps pass. Our verdict
            // does not — not because it is wrong, but because it is ours, and a
            // reader discounting our software discounts the numbers buried
            // underneath it.
            //
            // So: reproduction first (the operator's claim, never ours), then
            // the radio, then the readings, then our reading of them, clearly
            // labelled as ours and last. See #217.

            if (!string.IsNullOrWhiteSpace(reproduction))
            {
                sb.AppendLine("Reproduced outside JJ Flexible");
                sb.Append("  ").AppendLine(reproduction);
                sb.AppendLine();
            }

            if (station != null)
            {
                sb.AppendLine("Radio");
                foreach (string line in station) sb.Append("  ").AppendLine(line);
                sb.AppendLine();
            }

            if (build != null)
            {
                sb.AppendLine("Software");
                foreach (string line in build) sb.Append("  ").AppendLine(line);
                sb.AppendLine();
            }

            sb.AppendLine("Readings, in signal-path order");
            if (Facts == null || Facts.All.Count == 0)
            {
                sb.AppendLine("  Nothing was collected.");
            }
            else
            {
                foreach (DiagnosticFact f in Facts.All)
                    sb.Append("  ").AppendLine(f.EvidenceLine());
            }
            sb.AppendLine();

            sb.AppendLine("Stage by stage");
            foreach (StageResult s in Stages)
                sb.Append("  ").AppendLine(s.Line());
            sb.AppendLine();

            // LAST, and labelled as ours. Noel: "We need to tell them what we
            // find, but not force them to the conclusion. Give them info to
            // come to the conclusion." Our findings are welcome; stating them
            // as a DIAGNOSIS is what invites an argument about whether a
            // third-party application is qualified to judge somebody's radio.
            // Positioned and framed so a support engineer can discard this
            // section without discarding the measurements above it.
            sb.AppendLine("What JJ Flexible made of the above");
            sb.AppendLine("  This is our interpretation, not a measurement. The readings above");
            sb.AppendLine("  stand on their own.");
            sb.Append("  ").AppendLine(Headline());
            sb.Append("  ").AppendLine(Census());
            if (FirstBroken?.Rule != null)
                sb.Append("  Recognised by rule ").Append(FirstBroken.Rule.Id)
                  .Append(" at ").Append(FirstBroken.Title).AppendLine(".");
            sb.AppendLine();

            sb.AppendLine("How the readings were taken");
            sb.Append("  ").AppendLine(Rules?.Describe() ?? "No rules were loaded.");
            foreach (string p in RuleProblems) sb.Append("  ").AppendLine(p);

            return sb.ToString();
        }
    }

    /// <summary>
    /// Walks a chain: takes a ruleset and a set of facts and produces the
    /// verdict, the honest census and the evidence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing here knows what a transmit chain is. It knows stages, rules,
    /// conditions and facts, which is what makes the rules data rather than
    /// code — and what makes the whole thing testable without a radio, a window
    /// or a thread: hand it a ruleset and a hand-built fact set and every path
    /// through it is reachable from a unit test.
    /// </para>
    /// <para>
    /// <b>The engine never guesses.</b> A rule it cannot evaluate is counted as
    /// unmade, never as passed. That single choice is what keeps a report from
    /// quietly turning into "all good".
    /// </para>
    /// </remarks>
    public static class ChainAnalyzer
    {
        private enum RuleOutcome { Fired, Passed, NotApplicable, Unreadable }

        /// <summary>
        /// Run a ruleset against a set of facts. Never throws and never returns
        /// null — a diagnostic that crashes is worse than one that says it could
        /// not look.
        /// </summary>
        public static ChainReport Run(DiagnosticRuleSet rules, DiagnosticFacts facts)
        {
            rules = rules ?? new DiagnosticRuleSet();
            facts = facts ?? new DiagnosticFacts();
            var report = new ChainReport(rules, facts);

            foreach (DiagnosticStage stage in rules.Stages)
            {
                var result = new StageResult(stage);
                report.Stages.Add(result);

                // Is this stage part of the operator's path at all? A stage
                // whose applicability we cannot judge is unreadable, not absent
                // — we do not get to skip a stage because we could not tell.
                bool inPath = true;
                bool applicabilityUnreadable = false;
                foreach (Condition c in stage.Needs)
                {
                    Answer a = c.Test(facts, out string why);
                    if (a == Answer.Unreadable)
                    {
                        applicabilityUnreadable = true;
                        AddReason(result, why.Length != 0 ? why : "could not tell whether this stage applies");
                    }
                    else if (a == Answer.No)
                    {
                        inPath = false;
                        AddReason(result, DescribeNotInPath(c, facts));
                    }
                }

                if (!inPath && !applicabilityUnreadable)
                {
                    result.Verdict = StageVerdict.NotInPath;
                    continue;
                }

                if (stage.NotObservable.Length != 0)
                {
                    result.Verdict = StageVerdict.NotObservable;
                    result.Reasons.Clear();
                    AddReason(result, facts.Fill(stage.NotObservable));
                    continue;
                }

                if (applicabilityUnreadable)
                {
                    // Unreadable because we could not tell whether the stage
                    // even applies — which is a CONDITION we lack, not a stage
                    // that is invisible from this computer. With no radio
                    // connected, mic-source is unreadable and four stages land
                    // here; counting them as permanently blind would tell the
                    // operator their computer cannot see four stages when the
                    // truth is simply that nothing is plugged in.
                    result.Verdict = StageVerdict.NotObservable;
                    result.MeasurableLater = true;
                    continue;
                }

                foreach (DiagnosticRule rule in rules.RulesFor(stage.Number))
                {
                    RuleOutcome outcome = Evaluate(rule, facts, out string why);
                    switch (outcome)
                    {
                        case RuleOutcome.Unreadable:
                            result.ChecksUnreadable++;
                            report.ChecksUnreadable++;
                            AddReason(result, why);
                            break;

                        case RuleOutcome.Passed:
                            result.ChecksMade++;
                            report.ChecksMade++;
                            break;

                        case RuleOutcome.Fired:
                            result.ChecksMade++;
                            report.ChecksMade++;
                            // First rule to fire in file order speaks for the
                            // stage. Later ones are usually the same fault seen
                            // from further along.
                            if (result.Rule == null)
                            {
                                result.Rule = rule;
                                result.Message = facts.Fill(rule.Verdict);
                                result.Remedy = facts.Fill(rule.Fix);
                            }
                            break;

                        // NotApplicable costs nothing and is counted nowhere: it
                        // is not a check that failed, it is a check that was
                        // never about this operator's setup.
                    }

                    if (outcome == RuleOutcome.Fired || outcome == RuleOutcome.Passed)
                        CollectEvidence(result, rule, facts);
                }

                if (result.Rule != null) result.Verdict = StageVerdict.Broken;
                else if (result.ChecksMade > 0) result.Verdict = StageVerdict.Healthy;
                else if (result.ChecksUnreadable > 0) result.Verdict = StageVerdict.NotObservable;
                else
                {
                    // No rule applied here at all. Honest answer: nothing was
                    // tested. Reported as unobservable rather than healthy,
                    // because "we ran no checks" must never read as "we passed".
                    result.Verdict = StageVerdict.NotObservable;
                    result.MeasurableLater = true;
                    AddReason(result, stage.NothingToCheck.Length != 0
                        ? facts.Fill(stage.NothingToCheck)
                        : "no check in the rule file applies to this stage on your setup");
                }
            }

            foreach (StageResult s in report.Stages)
            {
                if (s.Verdict == StageVerdict.Broken) { report.FirstBroken = s; break; }
            }

            return report;
        }

        private static RuleOutcome Evaluate(DiagnosticRule rule, DiagnosticFacts facts, out string why)
        {
            why = "";

            foreach (Condition c in rule.Needs)
            {
                Answer a = c.Test(facts, out string w);
                if (a == Answer.Unreadable) { why = w; return RuleOutcome.Unreadable; }
                if (a == Answer.No) return RuleOutcome.NotApplicable;
            }

            // A rule with nothing to test is a rule that was never run, and it
            // must NOT count as a check that passed.
            //
            // This is not hypothetical. The parser drops any broken-when line it
            // cannot read and leaves the rule in place with an empty list, so
            // ONE typo in the rule file — or in an override file pushed out
            // after release, which is the documented delivery path — would
            // otherwise turn that rule into a silent pass. Stage 9 carries
            // exactly one rule, so a typo there would report the confirmed
            // silent-transmit failure as "checked, nothing wrong". The ruleset's
            // Problems list does name the bad line, but a list nobody reads is
            // not a defence; refusing to count the check is.
            if (rule.BrokenWhen.Count == 0)
            {
                why = "the check named " + rule.Id
                    + " has no test in the rule file, so it could not be run";
                return RuleOutcome.Unreadable;
            }

            bool all = true;
            foreach (Condition c in rule.BrokenWhen)
            {
                Answer a = c.Test(facts, out string w);
                if (a == Answer.Unreadable) { why = w; return RuleOutcome.Unreadable; }
                if (a == Answer.No) all = false;
            }

            // Every condition is evaluated even once one is false, so a rule
            // reports Unreadable when any part of it could not be read rather
            // than silently passing on the first false. A check we only half
            // made is a check we did not make.
            return all ? RuleOutcome.Fired : RuleOutcome.Passed;
        }

        private static void CollectEvidence(StageResult result, DiagnosticRule rule, DiagnosticFacts facts)
        {
            foreach (string name in rule.AllFactNames())
            {
                DiagnosticFact f = facts.Find(name);
                if (f == null) continue;
                if (!result.Evidence.Contains(f)) result.Evidence.Add(f);
            }
        }

        private static void AddReason(StageResult result, string reason)
        {
            if (string.IsNullOrEmpty(reason)) return;
            if (!result.Reasons.Contains(reason)) result.Reasons.Add(reason);
        }

        /// <summary>
        /// Say why a stage is not in the path in terms of what the operator
        /// chose, using the fact's own words rather than the condition's.
        /// </summary>
        private static string DescribeNotInPath(Condition c, DiagnosticFacts facts)
        {
            DiagnosticFact f = facts.Find(c.FactName);
            if (f == null) return "it needs " + c.Text;
            if (f.State != FactState.Observed) return "it needs " + c.Text;
            return "because " + f.Label.ToLowerInvariant() + " is "
                 + (f.TextValue.Length == 0 ? "empty" : f.TextValue);
        }
    }
}
