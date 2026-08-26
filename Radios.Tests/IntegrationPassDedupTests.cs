using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using static Radios.Tests.IntegrationPass;

namespace Radios.Tests
{
    /// <summary>
    /// Pass 1 of the integration pass: the concept-dedup sweep.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Looks for one idea implemented twice.</b> Two agents building the
    /// same thing in two files produce no merge conflict, no build error and
    /// two working implementations; git is content and so is the compiler. An
    /// orchestrator watching file assignments prevents COLLISIONS and every one
    /// of the five duplications found on 2026-08-25 sat in disjoint files.
    /// </para>
    /// <para>
    /// <b>Three signals, each tuned until it is worth reading.</b> A sweep that
    /// returns seventy candidates trains you to ignore it, which is worse than
    /// no sweep — CLAUDE.md says exactly this about a daily
    /// <c>--outdated</c> listing. So each rule below was measured against the
    /// real tree and narrowed until its output was small enough that a person
    /// will actually look at every line.
    /// </para>
    /// <para>
    /// <b>What this sweep cannot see, and who has to.</b> It matches text, so
    /// it finds copies and misses near-copies.
    /// <c>TxTuneProbeRunner.LooksBad</c> is a re-implementation of the
    /// <c>badNow</c> expression inside <c>TxTuneProbe.ShouldStopEarly</c> —
    /// same idea, same constants, different name, and the qualification differs
    /// by one <c>TxTuneProbe.</c> prefix, so no textual rule here catches it.
    /// #256 anticipated this: an agent with the whole tree and a specific
    /// question is good at it. The questions worth asking after a merge are:
    /// </para>
    /// <list type="bullet">
    /// <item>Which helpers exist that the next author built beside rather than
    /// called? <c>HostApiPhrase</c> had exactly one caller while a second site
    /// three files away concatenated the raw string.</item>
    /// <item>Where does the same decision get made from the same constants
    /// under two names?</item>
    /// <item>What capability was built this sprint that already existed?
    /// <c>SessionArchive</c> was already shipping while a design conversation
    /// worked out how to build one.</item>
    /// </list>
    /// <para>
    /// A single-caller sweep was built and DELIBERATELY NOT KEPT: even narrowed
    /// to helpers exposed beyond their own file, it returned twenty-nine
    /// candidates on this tree, most of them legitimate. It is recorded here as
    /// a question for a person rather than shipped as a rule nobody reads.
    /// </para>
    /// </remarks>
    public class IntegrationPassDedupTests
    {
        /// <summary>
        /// Authored, non-test C# and VB. Tests are excluded because a test that
        /// quotes the sentence it asserts on is doing its job, and a test's
        /// Dispose is boilerplate — including them buried the product findings
        /// under fixtures when this was first measured.
        /// </summary>
        private static IReadOnlyList<string> Corpus()
        {
            string[] files = IntegrationPassTree.AuthoredSource
                                                .Where(f => !IntegrationPassTree.IsTest(f))
                                                .ToArray();
            Assert.True(files.Length > 300,
                "only " + files.Length + " authored non-test source files were found, so this "
                + "sweep is not looking at the product and its silence means nothing.");
            return files;
        }

        // ═══════════════════════════════════════════════════════════════
        //  One sentence, written twice
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// An operator-facing sentence assembled independently in two files.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Two copies of a sentence are two places it has to be corrected, and
        /// the second one never is. That is not hypothetical here: the wording
        /// Noel corrected twice in one day was live in a second file the whole
        /// time.
        /// </para>
        /// <para>
        /// Forty characters and a run of lowercase words are what separate
        /// prose from a format string or an XML fragment. Placeholders are
        /// excluded because a shared template is usually the RIGHT answer and
        /// flagging it would punish the fix.
        /// </para>
        /// </remarks>
        [Fact]
        public void No_sentence_is_written_in_two_places()
        {
            var literal = new Regex("\"((?:[^\"\\\\\\n]|\\\\.){40,})\"");
            var homes = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            int scanned = 0;

            foreach (string file in Corpus())
            {
                string text = IntegrationPassTree.Read(file);
                foreach (Match m in literal.Matches(text))
                {
                    string s = m.Groups[1].Value;
                    if (s.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                    if (s.Contains('{') || s.Contains('<')) continue;         // templates and markup
                    if (!Regex.IsMatch(s, "[a-z] [a-z]")) continue;           // needs running words
                    scanned++;

                    if (!homes.TryGetValue(s, out SortedSet<string>? where))
                        homes[s] = where = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    where.Add(IntegrationPassTree.Relative(file));
                }
            }

            // POSITIVE CONTROL: the matcher has to be finding prose at all.
            Assert.True(scanned > 200,
                "only " + scanned + " prose literal(s) were collected across the tree, so the "
                + "matcher has stopped working and every file looks unique.");

            var findings = homes
                .Where(kv => kv.Value.Count > 1)
                .Select(kv => new Finding(Rules.DuplicateProse, Shorten(kv.Key),
                    "the same sentence is built in " + kv.Value.Count + " files: "
                    + string.Join(", ", kv.Value) + ". Correcting it in one leaves the other."));

            Gate(Rules.DuplicateProse,
                 "A sentence the operator reads should have one home. Two copies are two places "
                 + "to correct and only one of them ever gets corrected.",
                 findings);
        }

        /// <summary>
        /// The baseline key for a sentence: enough to identify it, short enough
        /// to sit in a list a person reads, and clean enough to type.
        /// </summary>
        /// <remarks>
        /// Trimmed and whitespace-collapsed on purpose. The first version cut
        /// the raw literal at a fixed width and produced keys ending in a
        /// trailing space, which a person transcribing the baseline has no way
        /// of seeing and every way of getting wrong. Two sentences sharing a
        /// 56-character prefix would collide; the baseline's own duplicate-key
        /// check is what catches that.
        /// </remarks>
        private static string Shorten(string s)
        {
            string clean = Regex.Replace(s, @"\s+", " ").Trim();
            return clean.Length <= 56 ? clean : clean.Substring(0, 56).TrimEnd();
        }

        // ═══════════════════════════════════════════════════════════════
        //  One method, implemented twice
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// The same method name carrying the same body in two files.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Bodies are compared after comments and whitespace are normalised
        /// away, so a copy that was reformatted or re-commented still matches.
        /// Names must agree too, which is what keeps this readable: comparing
        /// every body against every other body finds a great deal of two-line
        /// boilerplate and nothing anybody would act on.
        /// </para>
        /// <para>
        /// The consequence is that a copy someone RENAMED escapes. That is the
        /// documented hole in this rule, not an oversight — see the class
        /// remarks and <c>LooksBad</c>.
        /// </para>
        /// </remarks>
        [Fact]
        public void No_method_is_implemented_twice_under_the_same_name()
        {
            var byName = new Dictionary<string, Dictionary<string, SortedSet<string>>>(StringComparer.Ordinal);
            int bodies = 0;

            foreach (string file in Corpus())
            {
                if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                string text = IntegrationPassTree.Read(file);

                foreach ((string name, string body) in MethodBodies(text))
                {
                    bodies++;
                    if (!byName.TryGetValue(name, out Dictionary<string, SortedSet<string>>? shapes))
                        byName[name] = shapes = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
                    if (!shapes.TryGetValue(body, out SortedSet<string>? where))
                        shapes[body] = where = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    where.Add(IntegrationPassTree.Relative(file));
                }
            }

            // POSITIVE CONTROL: the body extractor has to be extracting bodies.
            Assert.True(bodies > 500,
                "only " + bodies + " method bodies were extracted from the whole tree, so the "
                + "extractor has stopped working and nothing can look duplicated.");

            var findings = new List<Finding>();
            foreach ((string name, Dictionary<string, SortedSet<string>> shapes) in byName)
                foreach ((string body, SortedSet<string> where) in shapes)
                {
                    if (where.Count < 2) continue;
                    findings.Add(new Finding(Rules.DuplicateBody, name,
                        name + " is implemented identically in " + where.Count + " files: "
                        + string.Join(", ", where) + " (" + body.Length + " characters). "
                        + "Neither copy knows about the other, so a fix to one is a divergence."));
                }

            Gate(Rules.DuplicateBody,
                 "One idea should have one implementation. Two copies do not conflict, do not "
                 + "fail to build, and both work — right up until one of them is corrected.",
                 findings);
        }

        /// <summary>
        /// Method declarations and their brace-matched bodies, normalised.
        /// </summary>
        /// <remarks>
        /// Deliberately crude and deliberately conservative: anything it cannot
        /// brace-match it drops, and short bodies are ignored, because a
        /// two-line property accessor being identical to another is not news.
        /// A missed method costs one unreported duplicate; a mis-parsed one
        /// would cost the rule its credibility.
        /// </remarks>
        private static IEnumerable<(string Name, string Body)> MethodBodies(string text)
        {
            var declaration = new Regex(
                @"\b(?:private|internal|public|protected)\s+(?:static\s+)?(?:async\s+)?"
                + @"[A-Za-z_][A-Za-z0-9_<>,\[\]\?\.]*\s+([A-Z][A-Za-z0-9_]*)\s*\([^)]*\)\s*\{");

            foreach (Match m in declaration.Matches(text))
            {
                int open = text.IndexOf('{', m.Index + m.Length - 1);
                if (open < 0) continue;

                int depth = 0, close = -1;
                for (int i = open; i < text.Length; i++)
                {
                    if (text[i] == '{') depth++;
                    else if (text[i] == '}' && --depth == 0) { close = i; break; }
                }
                if (close < 0) continue;

                string body = Normalise(text.Substring(open, close - open + 1));
                if (body.Length < 60) continue;
                yield return (m.Groups[1].Value, body);
            }
        }

        /// <summary>Comments and whitespace away, so a copy that was
        /// re-commented still reads as a copy.</summary>
        private static string Normalise(string body)
        {
            body = Regex.Replace(body, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            body = Regex.Replace(body, @"//[^\n]*", " ");
            return Regex.Replace(body, @"\s+", " ").Trim();
        }

        // ═══════════════════════════════════════════════════════════════
        //  Two words for one thing
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// One concept, one word, wherever the operator meets it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A curated list, and it has to be: no sweep can work out that "audio
        /// system" and "audio subsystem" are the same idea. What the sweep does
        /// is make the ruling stick once a person has made it. <c>AudioSetupCheck</c>
        /// noted this exact collision in its own remarks on 2026-08-25 and it
        /// is still live, which is what a note without a check is worth.
        /// </para>
        /// <para>
        /// <b>String literals only.</b> A comment may say whatever helps the
        /// reader; this is about what reaches the operator.
        /// </para>
        /// <para>
        /// Add a pair here whenever a wording ruling is made. The cost is one
        /// line and the alternative is the ruling being rediscovered.
        /// </para>
        /// </remarks>
        [Fact]
        public void One_concept_is_not_given_two_words()
        {
            (string A, string B, string Concept)[] pairs =
            {
                ("audio subsystem", "audio system", "the PortAudio host API"),
            };

            var findings = new List<Finding>();

            foreach ((string a, string b, string concept) in pairs)
            {
                SortedSet<string> usesA = FilesSayingInProse(a);
                SortedSet<string> usesB = FilesSayingInProse(b);

                // POSITIVE CONTROL, per pair: a wording that has vanished from
                // the tree must be struck off this list rather than left here
                // silently agreeing with everything.
                Assert.True(usesA.Count > 0 || usesB.Count > 0,
                    "neither \"" + a + "\" nor \"" + b + "\" appears in any operator-facing string "
                    + "any more, so this pair no longer describes the tree. Remove the line.");

                if (usesA.Count == 0 || usesB.Count == 0) continue;

                findings.Add(new Finding(Rules.CompetingVocabulary, a + " / " + b,
                    concept + " is called \"" + a + "\" in " + string.Join(", ", usesA)
                    + " and \"" + b + "\" in " + string.Join(", ", usesB)
                    + ". An operator meets both and has no way to know they are the same thing."));
            }

            Gate(Rules.CompetingVocabulary,
                 "One thing, one name. Two words for one concept make an operator wonder what "
                 + "the difference is, and there isn't one.",
                 findings);
        }

        /// <summary>Files whose operator-facing strings contain a phrase.</summary>
        private static SortedSet<string> FilesSayingInProse(string phrase)
        {
            var literal = new Regex("\"((?:[^\"\\\\\\n]|\\\\.)*)\"");
            var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string file in Corpus())
            {
                string text = IntegrationPassTree.Read(file);
                if (text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) < 0) continue;

                foreach (Match m in literal.Matches(text))
                    if (m.Groups[1].Value.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        found.Add(IntegrationPassTree.Relative(file));
                        break;
                    }
            }
            return found;
        }
    }
}
