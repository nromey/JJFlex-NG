using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Radios.ChainChecks
{
    /// <summary>
    /// What one test came out as. Three answers, not two, for the same reason
    /// the whole analyzer has three: a test we could not run is not a test that
    /// passed.
    /// </summary>
    public enum Answer
    {
        /// <summary>The condition holds.</summary>
        Yes,

        /// <summary>The condition does not hold.</summary>
        No,

        /// <summary>We could not tell — the fact it asks about is not observable
        /// from here, or nothing collected it.</summary>
        Unreadable
    }

    /// <summary>
    /// One test against one fact, written in words rather than symbols.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The comparisons are spelled "above", "below", "at least", "at most"
    /// rather than the usual symbols, because the rule file is meant to be
    /// read — and edited — with a screen reader, where a line of angle brackets
    /// and equals signs is punctuation soup and "mic-level below minus 50" is a
    /// sentence.
    /// </para>
    /// <para>
    /// The set is deliberately small. Every operator here earns its place by
    /// appearing in a rule that ships; anything richer belongs in a fact, where
    /// it can be computed once by code that can be read, rather than in an
    /// expression language nobody can debug.
    /// </para>
    /// </remarks>
    public sealed class Condition
    {
        private enum Op
        {
            Equals, NotEquals, Empty, NotEmpty, Contains,
            Below, Above, AtMost, AtLeast,
            IsSilent, IsAbsent, IsReadable, StaleOver
        }

        private Condition(string factName, Op op, string text, double number)
        {
            FactName = factName;
            _op = op;
            _text = text;
            _number = number;
        }

        private readonly Op _op;
        private readonly string _text;
        private readonly double _number;

        /// <summary>The fact this asks about.</summary>
        public string FactName { get; }

        /// <summary>The condition as it was written in the rule file, so a report
        /// can quote the actual test rather than paraphrase it.</summary>
        public string Text { get; private set; } = "";

        /// <summary>
        /// Read one condition line. Returns null and sets <paramref name="problem"/>
        /// when the line does not parse — never throws, because a typo in a rule
        /// file shipped to a user must degrade to one missing check, not to a
        /// dialog that will not open.
        /// </summary>
        public static Condition Parse(string line, out string problem)
        {
            problem = null;
            string s = (line ?? "").Trim();
            if (s.Length == 0) { problem = "empty condition"; return null; }

            int sp = s.IndexOf(' ');
            if (sp <= 0) { problem = "no test after the fact name in \"" + s + "\""; return null; }

            string fact = s.Substring(0, sp).Trim();
            string rest = s.Substring(sp + 1).Trim();
            string lower = rest.ToLowerInvariant();

            Condition Make(Op op, string t = "", double n = 0)
            {
                return new Condition(fact, op, t, n) { Text = s };
            }

            // Longest match first: "is not empty" must beat "is not <text>",
            // which must beat "is <text>".
            if (lower == "is not empty") return Make(Op.NotEmpty);
            if (lower == "is empty") return Make(Op.Empty);
            if (lower == "silent") return Make(Op.IsSilent);
            if (lower == "absent") return Make(Op.IsAbsent);
            if (lower == "readable") return Make(Op.IsReadable);

            if (lower.StartsWith("is not ", StringComparison.Ordinal))
                return Make(Op.NotEquals, rest.Substring(7).Trim());
            if (lower.StartsWith("is ", StringComparison.Ordinal))
                return Make(Op.Equals, rest.Substring(3).Trim());
            if (lower.StartsWith("contains ", StringComparison.Ordinal))
                return Make(Op.Contains, rest.Substring(9).Trim());

            if (TryNumeric(lower, rest, "below ", out double b)) return Make(Op.Below, "", b);
            if (TryNumeric(lower, rest, "above ", out double a)) return Make(Op.Above, "", a);
            if (TryNumeric(lower, rest, "at most ", out double m)) return Make(Op.AtMost, "", m);
            if (TryNumeric(lower, rest, "at least ", out double l)) return Make(Op.AtLeast, "", l);
            if (TryNumeric(lower, rest, "stale over ", out double st)) return Make(Op.StaleOver, "", st);

            problem = "unrecognised test \"" + rest + "\" in condition \"" + s + "\"";
            return null;
        }

        private static bool TryNumeric(string lower, string rest, string prefix, out double value)
        {
            value = 0;
            if (!lower.StartsWith(prefix, StringComparison.Ordinal)) return false;
            string tail = rest.Substring(prefix.Length).Trim();
            // "stale over 10 seconds" — the unit word is for the reader, not the
            // parser, so any trailing words after the number are ignored.
            int cut = tail.IndexOf(' ');
            if (cut > 0) tail = tail.Substring(0, cut);
            // "minus 50" is not accepted; write -50. Spelled-out negatives read
            // well aloud but would make the grammar ambiguous with unit words.
            return double.TryParse(tail, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// Test this condition against the collected facts.
        /// </summary>
        /// <remarks>
        /// Unreadable wins over everything. A missing fact, a fact this radio
        /// cannot report, and a meter that has never spoken all produce
        /// Unreadable for any test other than the ones that ask about those
        /// states directly — so a rule can never accidentally pass because the
        /// number it wanted defaulted to zero.
        /// </remarks>
        public Answer Test(DiagnosticFacts facts, out string why)
        {
            why = "";
            DiagnosticFact f = facts?.Find(FactName);
            if (f == null)
            {
                // A rule naming a fact nobody supplies is a gap between the rule
                // file and the fact sources, and it must read as unreadable
                // rather than as false — otherwise a mistyped fact name would
                // turn into a stage that silently always passes.
                why = "nothing in this build reports a reading called " + FactName;
                return Answer.Unreadable;
            }

            // The three state tests answer for every state; everything else
            // needs a value.
            switch (_op)
            {
                case Op.IsSilent:
                    return f.State == FactState.Silent ? Answer.Yes : Answer.No;
                case Op.IsAbsent:
                    return f.State == FactState.Absent ? Answer.Yes : Answer.No;
                case Op.IsReadable:
                    return f.State == FactState.Observed ? Answer.Yes : Answer.No;
            }

            if (f.State != FactState.Observed)
            {
                why = f.Why.Length != 0 ? f.Why : f.Label + " could not be read";
                return Answer.Unreadable;
            }

            switch (_op)
            {
                case Op.Empty:
                    return f.TextValue.Length == 0 ? Answer.Yes : Answer.No;
                case Op.NotEmpty:
                    return f.TextValue.Length != 0 ? Answer.Yes : Answer.No;
                case Op.Equals:
                    return string.Equals(f.TextValue, _text, StringComparison.OrdinalIgnoreCase)
                        ? Answer.Yes : Answer.No;
                case Op.NotEquals:
                    return string.Equals(f.TextValue, _text, StringComparison.OrdinalIgnoreCase)
                        ? Answer.No : Answer.Yes;
                case Op.Contains:
                    return f.TextValue.IndexOf(_text, StringComparison.OrdinalIgnoreCase) >= 0
                        ? Answer.Yes : Answer.No;
                case Op.StaleOver:
                    TimeSpan? age = f.Age;
                    if (age == null)
                    {
                        why = f.Label + " carries no timestamp, so its age cannot be judged";
                        return Answer.Unreadable;
                    }
                    return age.Value.TotalSeconds > _number ? Answer.Yes : Answer.No;
            }

            if (f.Number == null)
            {
                why = f.Label + " is not a number, so it cannot be compared";
                return Answer.Unreadable;
            }

            double v = f.Number.Value;
            switch (_op)
            {
                case Op.Below: return v < _number ? Answer.Yes : Answer.No;
                case Op.Above: return v > _number ? Answer.Yes : Answer.No;
                case Op.AtMost: return v <= _number ? Answer.Yes : Answer.No;
                case Op.AtLeast: return v >= _number ? Answer.Yes : Answer.No;
            }

            why = "the test in \"" + Text + "\" is not one this build understands";
            return Answer.Unreadable;
        }

        public override string ToString() => Text;
    }

    /// <summary>
    /// One failure the analyzer knows how to recognise, and what to say about it.
    /// </summary>
    public sealed class DiagnosticRule
    {
        /// <summary>Short identifier from the rule file. Appears in the evidence
        /// block so a report can be traced back to the rule that produced it.</summary>
        public string Id { get; internal set; } = "";

        /// <summary>Which stage of the chain this rule judges.</summary>
        public int StageNumber { get; internal set; }

        /// <summary>Conditions that must ALL hold before this rule applies at
        /// all. Use these for "only on a remote connection", never for the
        /// failure itself.</summary>
        public List<Condition> Needs { get; } = new List<Condition>();

        /// <summary>Conditions that must ALL hold for this rule to declare the
        /// stage broken.</summary>
        public List<Condition> BrokenWhen { get; } = new List<Condition>();

        /// <summary>What is wrong, in the operator's own words. One or two
        /// sentences, addressed to them, saying what the consequence is rather
        /// than naming a property.</summary>
        public string Verdict { get; internal set; } = "";

        /// <summary>What to do about it. Written as an instruction they can
        /// follow without knowing how the radio works.</summary>
        public string Fix { get; internal set; } = "";

        /// <summary>
        /// The other rules in this walk whose PASSING refutes the causes
        /// <see cref="Fix"/> names. When every one of them was actually made and
        /// did not fire, <see cref="FixWhenCleared"/> is said instead.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>#448, and #437 is the same shape.</b> A remedy is a static string
        /// chosen by rule id, written with no access to what the rest of the
        /// walk found — so stage 11 could tell an operator to check the mic
        /// profile and the microphone input a few lines under stage 9 and stage
        /// 8 reporting both <i>checked, nothing wrong</i>. Nothing failed: the
        /// stages were right, the rule was right, and the sentence was still
        /// false.
        /// </para>
        /// <para>
        /// <b>Passed is the only outcome that clears.</b> A named rule that
        /// fired, that did not apply, or that could not be read has NOT excluded
        /// its cause, and the ordinary remedy stands. Clearing on anything
        /// weaker would turn "we never looked" into "we ruled it out", which is
        /// the same lie the three-answer rule exists to prevent.
        /// </para>
        /// </remarks>
        public List<string> ClearedBy { get; } = new List<string>();

        /// <summary>
        /// The remedy to say when every rule in <see cref="ClearedBy"/> was made
        /// and passed. Where the walk has excluded every cause it can name, the
        /// honest instruction is to say so rather than to list them again.
        /// </summary>
        public string FixWhenCleared { get; internal set; } = "";

        /// <summary>Extra facts worth quoting in the evidence block when this
        /// rule fires, beyond the ones it tests.</summary>
        public List<string> Evidence { get; } = new List<string>();

        /// <summary>Every fact this rule reads, tested facts first, then the
        /// declared extras.</summary>
        public IEnumerable<string> AllFactNames()
        {
            foreach (Condition c in Needs) yield return c.FactName;
            foreach (Condition c in BrokenWhen) yield return c.FactName;
            foreach (string e in Evidence) yield return e;
        }

        public override string ToString() => Id + " (stage " + StageNumber + ")";
    }

    /// <summary>
    /// One stage of the chain being walked: a number, a name, and optionally a
    /// standing reason it cannot be seen from here at all.
    /// </summary>
    public sealed class DiagnosticStage
    {
        /// <summary>Position in the walk. The report follows this order, and the
        /// first broken stage is the answer.</summary>
        public int Number { get; internal set; }

        /// <summary>What this stage is, in the operator's words. Becomes part of
        /// every sentence about it, so "microphone chosen on this computer"
        /// rather than "input device selection".</summary>
        public string Name { get; internal set; } = "";

        /// <summary>One sentence on what this stage does, for an operator who
        /// wants to understand rather than just to be told.</summary>
        public string About { get; internal set; } = "";

        /// <summary>Set when this stage cannot be checked at all from this
        /// computer, with the reason. Declared in DATA rather than in code so
        /// that the day an observable appears, the stage becomes checkable by
        /// deleting one line.</summary>
        public string NotObservable { get; internal set; } = "";

        /// <summary>
        /// What to say when this stage applies but no rule in the file had
        /// anything to test — most often because the measurement only exists
        /// while transmitting. Without it the report falls back to a flat "no
        /// check applies", which is true and unhelpful; with it the stage says
        /// "transmit to measure", which is what the rest of the app says.
        /// </summary>
        public string NothingToCheck { get; internal set; } = "";

        /// <summary>Conditions that must hold for this stage to be part of this
        /// operator's path at all. A stage that fails these is reported as not
        /// in the path — which is a different answer from broken and a
        /// different answer from unreadable. The sentence names the chain from
        /// <see cref="DiagnosticRuleSet.ChainName"/>, so the receive walk says
        /// receive.</summary>
        public List<Condition> Needs { get; } = new List<Condition>();

        public override string ToString() => "Stage " + Number + ", " + Name;
    }

    /// <summary>
    /// A whole ruleset, read from a plain text file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Rules are data.</b> Nothing in this class knows what a transmit chain
    /// is; it knows about stages, rules, conditions and facts, plus the one word
    /// a chain is called by (<see cref="ChainName"/>), which is data too. The TX
    /// chain walk is one file. A receive-chain walk, an amplifier walk or a rule
    /// pushed out through the Data Provider to chase a fault we have not seen
    /// yet are all the same object with different text in it, and none of them
    /// needs a build.
    /// </para>
    /// <para>
    /// The format is documented in full at the top of the shipped rule file,
    /// which is the authority — a rule author should never have to read this
    /// code. See <c>Radios/ChainChecks/tx-chain-rules.txt</c>.
    /// </para>
    /// </remarks>
    public sealed class DiagnosticRuleSet
    {
        /// <summary>What this ruleset walks, in the operator's words.</summary>
        public string Title { get; private set; } = "Chain check";

        /// <summary>
        /// The word the report uses whenever it has to name this chain:
        /// "not part of your <b>transmit</b> path", "your <b>receive</b> chain".
        /// Supplied by whoever loaded the set, and overridden by a
        /// <c>chain:</c> line in the file itself.
        /// </summary>
        /// <remarks>
        /// <para>
        /// It exists because the analyzer is shared. Every sentence the engine
        /// writes for itself — a stage that is not in the path, a walk that
        /// checked everything, a walk that could check nothing — has to name
        /// the chain, and those sentences used to say "transmit" whichever
        /// ruleset was running. A receive report could therefore announce that
        /// something is not part of your transmit path, which is true of
        /// nothing the operator asked about.
        /// </para>
        /// <para>
        /// <b>One name in two frames, not two sets of sentences.</b> The fix
        /// deliberately is not a receive-flavoured copy of each line beside the
        /// transmit one: that is two vocabularies for one idea, which is the
        /// duplication this project keeps finding. Write the name lower case
        /// and as a bare word — it always appears mid-sentence, between "your"
        /// and "path" or "chain".
        /// </para>
        /// </remarks>
        public string ChainName { get; internal set; } = DefaultChainName;

        /// <summary>
        /// What a ruleset nobody named calls its chain. Deliberately
        /// direction-free: a set that was never told which way it points must
        /// not claim to be either one.
        /// </summary>
        public const string DefaultChainName = "signal";

        /// <summary>
        /// The operator's own complaint, as a clause the report can put after
        /// "If": "you are still not being heard", "you still cannot hear the
        /// radio". Set by a <c>symptom:</c> line in the rule file.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The second half of the same lesson <see cref="ChainName"/> records,
        /// found when the receive walk first reached the Fixer (#367). One
        /// sentence the engine writes for itself — the one it says when nothing
        /// fired but a check went unmade — still ended "if you are still not
        /// being heard", which is the transmit complaint and is true of nothing
        /// a receive walk was asked about. It was unreachable on the receive
        /// side only because the Workshop's call site substituted its own
        /// sentence for that whole branch (#370); joining the two doors made it
        /// live.
        /// </para>
        /// <para>
        /// Data, like the chain name, and for the same reason: a
        /// receive-flavoured copy of the sentence beside the transmit one is
        /// two vocabularies for one idea. Write it as a bare clause with no
        /// leading "if" and no full stop.
        /// </para>
        /// </remarks>
        public string Symptom { get; internal set; } = DefaultSymptom;

        /// <summary>
        /// What a ruleset that names no symptom says instead. True of any
        /// chain, and it claims nothing about a direction nobody stated — an
        /// override file written before the keyword existed gets this rather
        /// than a wrong guess.
        /// </summary>
        public const string DefaultSymptom = "the problem is still there";

        /// <summary>The ruleset's own version, so an evidence block can say which
        /// rules produced a verdict — including one delivered after release.</summary>
        public string Version { get; private set; } = "";

        /// <summary>Where these rules were read from, for the evidence block:
        /// the build, or an operator's own override file.</summary>
        public string Origin { get; internal set; } = "";

        /// <summary>The stages, in walk order.</summary>
        public List<DiagnosticStage> Stages { get; } = new List<DiagnosticStage>();

        /// <summary>The rules, in file order. Within a stage, the first rule that
        /// fires is the one that speaks.</summary>
        public List<DiagnosticRule> Rules { get; } = new List<DiagnosticRule>();

        /// <summary>
        /// Lines that did not parse, each with its line number. Never silently
        /// dropped: a report says how many rules it could not read, because a
        /// ruleset that quietly lost half its rules would report a healthy radio
        /// with total confidence.
        /// </summary>
        public List<string> Problems { get; } = new List<string>();

        /// <summary>
        /// Read a ruleset from text. Never throws and never returns null: a file
        /// that is entirely unparseable yields an empty ruleset whose
        /// <see cref="Problems"/> say so, and the report degrades to "no checks
        /// could be run" rather than to an exception in a diagnostic tool.
        /// </summary>
        /// <param name="text">The rule file's contents.</param>
        /// <param name="origin">Where it came from, for the evidence block.</param>
        /// <param name="chainName">
        /// What to call this chain if the file does not name itself — supplied
        /// by the loader, which knows which of the shipped walks it asked for.
        /// A <c>chain:</c> line inside the file wins over it, so an operator's
        /// own rule file can say what it walks, and one that predates the
        /// keyword still gets the right word. See <see cref="ChainName"/>.
        /// </param>
        public static DiagnosticRuleSet Parse(string text, string origin = "", string chainName = "")
        {
            var set = new DiagnosticRuleSet { Origin = origin ?? "" };
            if (!string.IsNullOrWhiteSpace(chainName)) set.ChainName = chainName.Trim();
            if (string.IsNullOrEmpty(text)) return set;

            DiagnosticStage stage = null;
            DiagnosticRule rule = null;
            int lineNo = 0;

            using (var reader = new StringReader(text))
            {
                string raw;
                while ((raw = reader.ReadLine()) != null)
                {
                    lineNo++;
                    string line = raw.Trim();
                    if (line.Length == 0) continue;
                    if (line[0] == '#') continue;

                    int colon = line.IndexOf(':');
                    if (colon <= 0)
                    {
                        set.Problems.Add("Line " + lineNo + ": no keyword before a colon.");
                        continue;
                    }

                    string key = line.Substring(0, colon).Trim().ToLowerInvariant();
                    string value = line.Substring(colon + 1).Trim();

                    switch (key)
                    {
                        case "ruleset":
                            set.Title = value;
                            stage = null; rule = null;
                            continue;
                        case "version":
                            set.Version = value;
                            continue;

                        case "chain":
                            // The one word every sentence the ENGINE writes for
                            // itself has to fill in. An empty one would leave a
                            // hole mid-sentence — "not part of your  path" —
                            // so it is refused and the loader's name stands.
                            if (value.Length == 0)
                            {
                                set.Problems.Add("Line " + lineNo + ": chain wants the operator's word "
                                    + "for what this file walks, as in \"chain: receive\".");
                                continue;
                            }
                            set.ChainName = value;
                            continue;

                        case "symptom":
                            // The operator's complaint as a clause, for the one
                            // sentence that has to say what "still wrong" means
                            // on this walk. Empty is refused for the same
                            // reason an empty chain name is: it would leave a
                            // hole mid-sentence.
                            if (value.Length == 0)
                            {
                                set.Problems.Add("Line " + lineNo + ": symptom wants the operator's "
                                    + "own complaint as a clause, as in \"symptom: you still cannot "
                                    + "hear the radio\".");
                                continue;
                            }
                            set.Symptom = value;
                            continue;

                        case "stage":
                        {
                            rule = null;
                            stage = ParseStageHeader(value, lineNo, set);
                            if (stage != null) set.Stages.Add(stage);
                            continue;
                        }

                        case "rule":
                            stage = null;
                            rule = new DiagnosticRule { Id = value };
                            set.Rules.Add(rule);
                            continue;

                        case "in-stage":
                            if (rule == null) { set.Problems.Add(WrongPlace(lineNo, key, "a rule")); continue; }
                            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                                rule.StageNumber = n;
                            else
                                set.Problems.Add("Line " + lineNo + ": in-stage wants a number, got \"" + value + "\".");
                            continue;

                        case "about":
                            if (stage != null) stage.About = Join(stage.About, value);
                            else set.Problems.Add(WrongPlace(lineNo, key, "a stage"));
                            continue;

                        case "not-observable":
                            if (stage != null) stage.NotObservable = Join(stage.NotObservable, value);
                            else set.Problems.Add(WrongPlace(lineNo, key, "a stage"));
                            continue;

                        case "nothing-to-check":
                            if (stage != null) stage.NothingToCheck = Join(stage.NothingToCheck, value);
                            else set.Problems.Add(WrongPlace(lineNo, key, "a stage"));
                            continue;

                        case "needs":
                        {
                            Condition c = Condition.Parse(value, out string problem);
                            if (c == null) { set.Problems.Add("Line " + lineNo + ": " + problem + "."); continue; }
                            if (rule != null) rule.Needs.Add(c);
                            else if (stage != null) stage.Needs.Add(c);
                            else set.Problems.Add(WrongPlace(lineNo, key, "a stage or a rule"));
                            continue;
                        }

                        case "broken-when":
                        {
                            if (rule == null) { set.Problems.Add(WrongPlace(lineNo, key, "a rule")); continue; }
                            Condition c = Condition.Parse(value, out string problem);
                            if (c == null) { set.Problems.Add("Line " + lineNo + ": " + problem + "."); continue; }
                            rule.BrokenWhen.Add(c);
                            continue;
                        }

                        case "verdict":
                            if (rule == null) { set.Problems.Add(WrongPlace(lineNo, key, "a rule")); continue; }
                            rule.Verdict = Join(rule.Verdict, value);
                            continue;

                        case "fix":
                            if (rule == null) { set.Problems.Add(WrongPlace(lineNo, key, "a rule")); continue; }
                            rule.Fix = Join(rule.Fix, value);
                            continue;

                        case "cleared-by":
                            if (rule == null) { set.Problems.Add(WrongPlace(lineNo, key, "a rule")); continue; }
                            foreach (string part in value.Split(','))
                            {
                                string p = part.Trim();
                                if (p.Length != 0 && !rule.ClearedBy.Contains(p)) rule.ClearedBy.Add(p);
                            }
                            continue;

                        case "fix-when-cleared":
                            if (rule == null) { set.Problems.Add(WrongPlace(lineNo, key, "a rule")); continue; }
                            rule.FixWhenCleared = Join(rule.FixWhenCleared, value);
                            continue;

                        case "evidence":
                            if (rule == null) { set.Problems.Add(WrongPlace(lineNo, key, "a rule")); continue; }
                            foreach (string part in value.Split(','))
                            {
                                string p = part.Trim();
                                if (p.Length != 0) rule.Evidence.Add(p);
                            }
                            continue;

                        default:
                            set.Problems.Add("Line " + lineNo + ": \"" + key + "\" is not a keyword this build knows.");
                            continue;
                    }
                }
            }

            set.Validate();
            return set;
        }

        private static string WrongPlace(int lineNo, string key, string owner)
        {
            return "Line " + lineNo + ": " + key + " has to come after " + owner + " line.";
        }

        /// <summary>Repeated text keys join with a space, so a long verdict can be
        /// written over several short lines — which is how it stays readable in a
        /// file meant to be edited with a screen reader.</summary>
        private static string Join(string existing, string addition)
        {
            if (string.IsNullOrEmpty(existing)) return addition;
            if (string.IsNullOrEmpty(addition)) return existing;
            return existing + " " + addition;
        }

        private static DiagnosticStage ParseStageHeader(string value, int lineNo, DiagnosticRuleSet set)
        {
            string v = (value ?? "").Trim();
            int sp = v.IndexOf(' ');
            string numberPart = sp > 0 ? v.Substring(0, sp) : v;
            string namePart = sp > 0 ? v.Substring(sp + 1).Trim() : "";

            if (!int.TryParse(numberPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
            {
                set.Problems.Add("Line " + lineNo + ": a stage line reads \"stage: <number> <name>\", got \"" + v + "\".");
                return null;
            }
            if (namePart.Length == 0)
            {
                set.Problems.Add("Line " + lineNo + ": stage " + number + " has no name.");
                return null;
            }
            return new DiagnosticStage { Number = number, Name = namePart };
        }

        /// <summary>
        /// Catch the mistakes that would otherwise show up as a silently missing
        /// check: a rule pointing at a stage that does not exist, a rule with
        /// nothing to test, a rule with nothing to say.
        /// </summary>
        private void Validate()
        {
            // A duplicate stage number is DROPPED, not merely reported. Left in
            // place it is walked twice and its rules counted twice, so the
            // census claims more checks than were ever run — and a census that
            // overstates how much was looked at is the exact lie this design
            // exists to prevent.
            var known = new HashSet<int>();
            for (int i = 0; i < Stages.Count; i++)
            {
                if (known.Add(Stages[i].Number)) continue;
                Problems.Add("Stage " + Stages[i].Number + " is declared more than once; "
                    + "only the first declaration is used.");
                Stages.RemoveAt(i);
                i--;
            }

            // A rule id has to be unique, because cleared-by resolves a cause to
            // the rule that excludes it BY NAME, and the evidence block traces a
            // verdict back the same way. Two rules sharing an id would make both
            // ambiguous silently.
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DiagnosticRule r in Rules)
                if (r.Id.Length != 0 && !ids.Add(r.Id))
                    Problems.Add("Rule " + r.Id + " is declared more than once, so naming it "
                        + "in a cleared-by line or in the evidence block is ambiguous.");

            foreach (DiagnosticRule r in Rules)
            {
                if (!known.Contains(r.StageNumber))
                    Problems.Add("Rule " + r.Id + " is in stage " + r.StageNumber + ", which is not declared.");
                if (r.BrokenWhen.Count == 0)
                    Problems.Add("Rule " + r.Id + " has no broken-when test, so it can never fire.");
                if (r.Verdict.Length == 0)
                    Problems.Add("Rule " + r.Id + " has no verdict, so it would fire and say nothing.");

                // A cleared-by naming a rule that does not exist can NEVER
                // clear, so the ordinary remedy stands for ever and the author
                // believes they fixed it. Say so out loud.
                foreach (string cleared in r.ClearedBy)
                {
                    if (string.Equals(cleared, r.Id, StringComparison.OrdinalIgnoreCase))
                        Problems.Add("Rule " + r.Id + " names itself in cleared-by, which can never "
                            + "hold: a rule that fired did not pass.");
                    else if (!ids.Contains(cleared))
                        Problems.Add("Rule " + r.Id + " says it is cleared by " + cleared
                            + ", which is not a rule in this file, so its remedy can never clear.");
                }

                if (r.ClearedBy.Count != 0 && r.FixWhenCleared.Length == 0)
                    Problems.Add("Rule " + r.Id + " has a cleared-by line and no fix-when-cleared, "
                        + "so there is nothing to say once those checks come back clean.");

                if (r.ClearedBy.Count == 0 && r.FixWhenCleared.Length != 0)
                    Problems.Add("Rule " + r.Id + " has a fix-when-cleared and no cleared-by, "
                        + "so nothing can ever make it the remedy.");
            }

            Stages.Sort((a, b) => a.Number.CompareTo(b.Number));
        }

        /// <summary>Every rule for one stage, in file order.</summary>
        public List<DiagnosticRule> RulesFor(int stageNumber)
        {
            var found = new List<DiagnosticRule>();
            foreach (DiagnosticRule r in Rules)
                if (r.StageNumber == stageNumber) found.Add(r);
            return found;
        }

        /// <summary>
        /// Every fact name any rule in this set mentions. A fact source can be
        /// checked against this to find facts nobody supplies — which is the one
        /// failure the engine cannot distinguish from a genuinely unobservable
        /// stage.
        /// </summary>
        public IReadOnlyList<string> RequiredFactNames()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            foreach (DiagnosticStage s in Stages)
                foreach (Condition c in s.Needs)
                    if (seen.Add(c.FactName)) order.Add(c.FactName);
            foreach (DiagnosticRule r in Rules)
                foreach (string name in r.AllFactNames())
                    if (seen.Add(name)) order.Add(name);
            return order;
        }

        /// <summary>A short account of what was loaded, for the evidence block.</summary>
        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append(Title);
            if (Version.Length != 0) sb.Append(", rules version ").Append(Version);
            if (Origin.Length != 0) sb.Append(", from ").Append(Origin);
            sb.Append(": ").Append(Stages.Count).Append(" stages, ").Append(Rules.Count).Append(" rules");
            if (Problems.Count != 0) sb.Append(", ").Append(Problems.Count).Append(" could not be read");
            sb.Append('.');
            return sb.ToString();
        }
    }
}
