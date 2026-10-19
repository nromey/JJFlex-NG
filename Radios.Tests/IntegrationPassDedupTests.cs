using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
                ParsedFile parsed = new ParsedFile();
                foreach (Match m in literal.Matches(text))
                {
                    // Text quoted inside a comment is not a sentence anyone hears.
                    // Added 2026-10-19 when the FlexBase split put two comments that
                    // quote one vendor error ("Invalid state for application
                    // registration") into different files and this rule called them
                    // a duplicated operator sentence.
                    if (NeverSpoken(file, text, m.Index, parsed)) continue;
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
        /// The preprocessor symbols a real build of this tree defines, Debug
        /// and Release. Read 2026-10-19 by evaluating <c>Radios.csproj</c>'s
        /// DefineConstants through the SDK: <c>TRACE;DEBUG;NET;NET10_0;
        /// NETCOREAPP;WINDOWS;WINDOWS7_0</c> plus the <c>_OR_GREATER</c>
        /// family. No authored project adds its own;
        /// <see cref="No_project_defines_a_symbol_this_scan_does_not_know"/>
        /// fails the day one does.
        /// </summary>
        private static readonly string[] ReleaseSymbols = BuildSymbols(debug: false);
        private static readonly string[] DebugSymbols = BuildSymbols(debug: true);

        private static string[] BuildSymbols(bool debug)
        {
            var list = new List<string> { "TRACE", "NET", "NET10_0", "NETCOREAPP", "WINDOWS", "WINDOWS7_0" };
            if (debug) list.Add("DEBUG");
            for (int v = 5; v <= 10; v++) list.Add("NET" + v + "_0_OR_GREATER");
            foreach (string c in new[] { "1_0", "1_1", "2_0", "2_1", "2_2", "3_0", "3_1" })
                list.Add("NETCOREAPP" + c + "_OR_GREATER");
            list.Add("WINDOWS7_0_OR_GREATER");
            return list.ToArray();
        }

        /// <summary>The two parses of one file, made on first need.</summary>
        private sealed class ParsedFile
        {
            public SyntaxNode? Debug;
            public SyntaxNode? Release;
        }

        /// <summary>
        /// True when the text at <paramref name="index"/> can never be heard: it
        /// is in a comment, in a preprocessor directive line, or in code that
        /// is switched off in BOTH the Debug and the Release build. Decided by
        /// the C# parser. For VB, a line that opens with <c>'</c> or <c>REM</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>History, because each version failed a different way.</b> The
        /// first counted quote characters to decide whether <c>//</c> opened a
        /// comment; Codex (2026-10-19) built valid source where an escaped
        /// quote made a real sentence vanish. The second asked Roslyn for
        /// comment trivia only, and Codex then found that text inside a
        /// switched-off <c>#if</c> block (<c>JJLogIO/logio/ScreenSaver.cs</c>,
        /// <c>#if dontIgnore</c>) was still counted as prose.
        /// </para>
        /// <para>
        /// <b>Why "both builds", not "this parse".</b> Parsed with no symbols, a
        /// <c>#if DEBUG</c> block looks switched off, and skipping it would hide
        /// every sentence a Debug build speaks \— the dangerous direction. So
        /// text is skipped only when it is disabled under the Debug symbols AND
        /// under the Release symbols. A file's own <c>#define</c> lines are
        /// honoured by the parser in both.
        /// </para>
        /// </remarks>
        private static bool NeverSpoken(string file, string text, int index, ParsedFile parsed)
        {
            if (file.EndsWith(".vb", StringComparison.OrdinalIgnoreCase))
            {
                int lineStart = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
                string lead = text.Substring(lineStart, index - lineStart).TrimStart();
                return lead.StartsWith("'", StringComparison.Ordinal)
                    || lead.StartsWith("REM ", StringComparison.OrdinalIgnoreCase);
            }

            parsed.Release ??= CSharpSyntaxTree.ParseText(text,
                new CSharpParseOptions(preprocessorSymbols: ReleaseSymbols)).GetRoot();
            SyntaxTrivia inRelease = parsed.Release.FindTrivia(index, findInsideTrivia: false);
            if (IsComment(inRelease) || inRelease.IsDirective) return true;
            if (!inRelease.IsKind(SyntaxKind.DisabledTextTrivia)) return false;

            parsed.Debug ??= CSharpSyntaxTree.ParseText(text,
                new CSharpParseOptions(preprocessorSymbols: DebugSymbols)).GetRoot();
            SyntaxTrivia inDebug = parsed.Debug.FindTrivia(index, findInsideTrivia: false);
            return inDebug.IsKind(SyntaxKind.DisabledTextTrivia) || IsComment(inDebug);
        }

        private static bool IsComment(SyntaxTrivia t)
            => t.IsKind(SyntaxKind.SingleLineCommentTrivia)
            || t.IsKind(SyntaxKind.MultiLineCommentTrivia)
            || t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
            || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia);

        /// <summary>
        /// The filter must skip what is never heard and NOTHING ELSE. Cases
        /// that must stay COUNTED are the dangerous ones: each is a real
        /// sentence a shipped build can speak.
        /// </summary>
        /// <remarks>
        /// The first case is Codex's first counterexample verbatim; the
        /// <c>dontIgnore</c> case is the shape of its second, from
        /// <c>ScreenSaver.cs</c>; the <c>#if DEBUG</c> case pins the hazard a
        /// careless fix for the second would create.
        /// </remarks>
        [Theory]
        [InlineData("x.cs", "string prefix = \"quoted \\\" // still in a string\"; string message = \"The remote radio is unavailable because the account has no online radios.\";", "The remote radio", false)]
        [InlineData("x.cs", "var p = @\"C:\\temp\"\" // \"; string message = \"A real sentence after a verbatim string with a doubled quote.\";", "A real", false)]
        [InlineData("x.cs", "char c = '\"'; string message = \"A real sentence after a character literal that is a quote mark.\";", "A real", false)]
        [InlineData("x.cs", "// \"This text is quoted in an ordinary comment and nobody hears it.\"", "This text", true)]
        [InlineData("x.cs", "/// <summary>\"This text is quoted in a documentation comment, not spoken.\"</summary>\nclass C {}", "This text", true)]
        [InlineData("x.cs", "/* \"This text is quoted inside a block comment and is never spoken.\" */", "This text", true)]
        [InlineData("x.cs", "#if dontIgnore\nvar s = \"This text sits in a block that no build ever switches on.\";\n#endif", "This text", true)]
        [InlineData("x.cs", "#if DEBUG\nvar s = \"A real sentence a Debug build speaks must still be counted here.\";\n#endif", "A real", false)]
        [InlineData("x.cs", "#if !DEBUG\nvar s = \"A real sentence only a Release build speaks is still counted.\";\n#endif", "A real", false)]
        [InlineData("x.cs", "#define CWMonitor\n#if CWMonitor\nvar s = \"A real sentence under a switch its own file turns on.\";\n#endif", "A real", false)]
        [InlineData("x.cs", "#region \"This text is quoted on a region line and nobody hears it.\"\n#endregion", "This text", true)]
        [InlineData("x.vb", "' \"This text is quoted in a VB comment and nobody hears it at all.\"", "This text", true)]
        [InlineData("x.vb", "Dim s = \"A real VB sentence that the rule must keep on counting.\"", "A real", false)]
        public void The_filter_skips_only_what_is_never_heard(string file, string source, string target, bool expected)
        {
            int at = source.LastIndexOf(target, StringComparison.Ordinal) - 1;   // the opening quote
            Assert.True(at >= 0 && source[at] == '"', "fixture is malformed: no quote before the target");
            Assert.Equal(expected, NeverSpoken(file, source, at, new ParsedFile()));
        }

        /// <summary>
        /// <see cref="NeverSpoken"/> is only safe while it knows every symbol a
        /// build can define. Today no authored project file adds one; a vendor
        /// project under <c>FlexLib_API/</c> sets only DEBUG and TRACE, which
        /// are already known. The day an authored project adds its own, this
        /// fails rather than letting text under that symbol be skipped as
        /// switched off.
        /// </summary>
        [Fact]
        public void No_project_defines_a_symbol_this_scan_does_not_know()
        {
            string root = IntegrationPassTree.Root;
            var known = new HashSet<string>(DebugSymbols, StringComparer.Ordinal) { "$(DefineConstants)" };
            var define = new Regex("<DefineConstants>([^<]*)</DefineConstants>");
            int projects = 0;
            var unknown = new List<string>();
            foreach (string path in Directory.EnumerateFiles(root, "*.*proj", SearchOption.AllDirectories)
                         .Concat(Directory.EnumerateFiles(root, "*.props", SearchOption.AllDirectories)))
            {
                string rel = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (rel.Contains("/bin/") || rel.Contains("/obj/") || rel.StartsWith("bin/") || rel.StartsWith("obj/")) continue;
                projects++;
                foreach (Match m in define.Matches(File.ReadAllText(path)))
                    foreach (string sym in m.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        if (!known.Contains(sym)) unknown.Add(rel + ": " + sym);
            }
            Assert.True(projects > 20, "only " + projects + " project files were found, so this guard is not looking at the tree.");
            Assert.True(unknown.Count == 0,
                "a project defines a preprocessor symbol the duplicate-sentence scan does not know, so text under it "
                + "could be skipped as switched off when a real build turns it on. Add it to BuildSymbols: "
                + string.Join("; ", unknown));
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
