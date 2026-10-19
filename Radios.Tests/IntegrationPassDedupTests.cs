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
                    if (NeverSpoken(file, text, m.Index, parsed, BuildText.Value)) continue;
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
        /// The text of every file that can define a preprocessor symbol for a
        /// build of this tree: project files, props, targets and build
        /// scripts, and MSBuild response files (Directory.Build.rsp), which can pass
        /// -p:DefineConstants on a command line no project file shows. Read once. Not
        /// covered, by design: a symbol supplied only through an environment variable
        /// or typed by hand on one developer's command line.
        /// </summary>
        private static readonly Lazy<string> BuildText = new Lazy<string>(() =>
        {
            string root = IntegrationPassTree.Root;
            var patterns = new[] { "*.csproj", "*.vbproj", "*.props", "*.targets", "*.rsp", "*.bat", "*.cmd", "*.ps1" };
            var sb = new System.Text.StringBuilder();
            int n = 0;
            foreach (string pattern in patterns)
                foreach (string path in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(root, path).Replace('\\', '/');
                    if (rel.Contains("/bin/") || rel.Contains("/obj/") || rel.StartsWith("bin/") || rel.StartsWith("obj/")) continue;
                    sb.Append(File.ReadAllText(path)).Append('\n');
                    n++;
                }
            if (n < 20)
                throw new InvalidOperationException("only " + n + " build files were found, so no symbol can be judged never-defined.");
            return sb.ToString();
        });

        /// <summary>
        /// Could some build of this tree define <paramref name="symbol"/>? Yes
        /// if it is spelled in capitals, digits and underscores (every symbol
        /// the SDK supplies is: DEBUG, RELEASE, TRACE, NET10_0, WINDOWS, and
        /// whatever a later SDK adds), or if the name appears anywhere in any
        /// build file or script, however it is attributed or conditioned.
        /// Only a name found nowhere in the build can be called never defined.
        /// </summary>
        internal static bool CouldBeDefinedByABuild(string symbol, string buildText)
        {
            if (symbol == "true" || symbol == "false") return false;
            if (symbol.All(c => char.IsUpper(c) || char.IsDigit(c) || c == '_')) return true;
            return Regex.IsMatch(buildText, @"\b" + Regex.Escape(symbol) + @"\b");
        }

        private static readonly Regex ConditionLine =
            new Regex(@"^[ \t]*#[ \t]*(?:if|elif)\b(.*)$", RegexOptions.Multiline);

        /// <summary>The parses of one file, keyed by the symbols defined, made on first need.</summary>
        private sealed class ParsedFile
        {
            public readonly Dictionary<string, SyntaxNode> Roots = new Dictionary<string, SyntaxNode>(StringComparer.Ordinal);
            public string[]? Live;

            public SyntaxNode Under(string text, string[] symbols)
            {
                string key = string.Join(";", symbols);
                if (!Roots.TryGetValue(key, out SyntaxNode? root))
                    Roots[key] = root = CSharpSyntaxTree.ParseText(text,
                        new CSharpParseOptions(preprocessorSymbols: symbols)).GetRoot();
                return root;
            }
        }

        /// <summary>
        /// True when the text at <paramref name="index"/> can never be heard: it
        /// is in a comment, in a preprocessor directive line, or in code that is
        /// switched off under EVERY combination of the symbols a build could
        /// define. Decided by the C# parser. For VB, a line that opens with
        /// <c>'</c> or <c>REM</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Every earlier version guessed which symbols a build defines, and
        /// Codex found a hole in each guess (2026-10-19):</b> quote-counting
        /// broke on an escaped quote; a comment-only check counted
        /// <c>#if dontIgnore</c> prose in <c>ScreenSaver.cs</c>; a Debug-only
        /// symbol list missed RELEASE; a typed list missed attributed
        /// <c>DefineConstants</c> and the six projects that target plain
        /// <c>net10.0</c>; and any fixed list breaks on a negation such as
        /// <c>#if !WINDOWS</c>.
        /// </para>
        /// <para>
        /// <b>So this one does not guess.</b> It collects the symbols in this
        /// file's <c>#if</c>/<c>#elif</c> lines that some build COULD define
        /// (<see cref="CouldBeDefinedByABuild"/>), and skips the text only if it
        /// is switched off under every combination of them on and off. No build
        /// configuration can then speak it. A symbol nothing could define, like
        /// <c>dontIgnore</c>, stays off throughout. A file's own <c>#define</c>
        /// and <c>#undef</c> are honoured by the parser. With more than eight
        /// such symbols in one file it gives up and COUNTS the text: the safe
        /// direction is the only one an error may take.
        /// </para>
        /// </remarks>
        private static bool NeverSpoken(string file, string text, int index, ParsedFile parsed, string buildText)
        {
            if (file.EndsWith(".vb", StringComparison.OrdinalIgnoreCase))
            {
                int lineStart = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
                string lead = text.Substring(lineStart, index - lineStart).TrimStart();
                return lead.StartsWith("'", StringComparison.Ordinal)
                    || lead.StartsWith("REM ", StringComparison.OrdinalIgnoreCase);
            }

            SyntaxTrivia none = parsed.Under(text, Array.Empty<string>()).FindTrivia(index, findInsideTrivia: false);
            if (IsComment(none) || none.IsDirective) return true;
            if (!none.IsKind(SyntaxKind.DisabledTextTrivia)) return false;

            parsed.Live ??= ConditionLine.Matches(text)
                .SelectMany(m => Regex.Matches(m.Groups[1].Value, "[A-Za-z_][A-Za-z0-9_]*").Select(x => x.Value))
                .Distinct(StringComparer.Ordinal)
                .Where(sym => CouldBeDefinedByABuild(sym, buildText))
                .OrderBy(sym => sym, StringComparer.Ordinal)
                .ToArray();
            string[] live = parsed.Live;
            if (live.Length > 8) return false;

            for (int mask = 1; mask < (1 << live.Length); mask++)
            {
                string[] on = live.Where((_, i) => (mask & (1 << i)) != 0).ToArray();
                SyntaxTrivia t = parsed.Under(text, on).FindTrivia(index, findInsideTrivia: false);
                if (!t.IsKind(SyntaxKind.DisabledTextTrivia) && !IsComment(t) && !t.IsDirective) return false;
            }
            return true;
        }

        private static bool IsComment(SyntaxTrivia t)
            => t.IsKind(SyntaxKind.SingleLineCommentTrivia)
            || t.IsKind(SyntaxKind.MultiLineCommentTrivia)
            || t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
            || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia);

        /// <summary>A project file that defines a lower-case symbol only for Release, attributed — the shape of Codex's fourth counterexample.</summary>
        private const string FixtureBuildText =
            "<Project><PropertyGroup>"
            + "<DefineConstants Condition=\"'$(Configuration)' == 'Release'\">$(DefineConstants);spokenOnly</DefineConstants>"
            + "</PropertyGroup></Project>";

        /// <summary>
        /// The filter must skip what is never heard and NOTHING ELSE. Every case
        /// expected to be COUNTED is a sentence some build can speak.
        /// </summary>
        /// <remarks>
        /// One case per hole Codex found on 2026-10-19, in order: the escaped
        /// quote; <c>#if dontIgnore</c> from <c>ScreenSaver.cs</c>; RELEASE;
        /// an attributed, Release-only project symbol (<c>spokenOnly</c>); and
        /// the negation and unknown-SDK-symbol cases a fixed list gets wrong.
        /// </remarks>
        [Theory]
        [InlineData("x.cs", "string prefix = \"quoted \\\" // still in a string\"; string message = \"The remote radio is unavailable because the account has no online radios.\";", "The remote radio", false)]
        [InlineData("x.cs", "var p = @\"C:\\temp\"\" // \"; string message = \"A real sentence after a verbatim string with a doubled quote.\";", "A real", false)]
        [InlineData("x.cs", "char c = '\"'; string message = \"A real sentence after a character literal that is a quote mark.\";", "A real", false)]
        [InlineData("x.cs", "// \"This text is quoted in an ordinary comment and nobody hears it.\"", "This text", true)]
        [InlineData("x.cs", "/// <summary>\"This text is quoted in a documentation comment, not spoken.\"</summary>\nclass C {}", "This text", true)]
        [InlineData("x.cs", "/* \"This text is quoted inside a block comment and is never spoken.\" */", "This text", true)]
        [InlineData("x.cs", "#region \"This text is quoted on a region line and nobody hears it.\"\n#endregion", "This text", true)]
        [InlineData("x.cs", "#if dontIgnore\nvar s = \"This text sits in a block that no build ever switches on.\";\n#endif", "This text", true)]
        [InlineData("x.cs", "#if dontIgnore && DEBUG\nvar s = \"This text needs a symbol no build defines, so it is dead.\";\n#endif", "This text", true)]
        [InlineData("x.cs", "#if dontIgnore || DEBUG\nvar s = \"A real sentence a Debug build speaks through the second half.\";\n#endif", "A real", false)]
        [InlineData("x.cs", "#if DEBUG\nvar s = \"A real sentence a Debug build speaks must still be counted here.\";\n#endif", "A real", false)]
        [InlineData("x.cs", "#if !DEBUG\nvar s = \"A real sentence only a Release build speaks is still counted.\";\n#endif", "A real", false)]
        [InlineData("x.cs", "#if RELEASE\nvar s = \"A real sentence a Release build speaks must still be counted.\";\n#endif", "A real", false)]
        [InlineData("x.cs", "#if !WINDOWS\nvar s = \"A real sentence a plain net10.0 build speaks is still counted.\";\n#endif", "A real", false)]
        [InlineData("x.cs", "#if WINDOWS10_0_19041\nvar s = \"A real sentence under a symbol a later SDK might supply.\";\n#endif", "A real", false)]
        [InlineData("x.cs", "#if FOO && !BAR\nvar s = \"A real sentence one combination of two symbols switches on.\";\n#endif", "A real", false)]
        [InlineData("x.cs", "#if spokenOnly\nvar s = \"A real sentence a Release-only project symbol switches on.\";\n#endif", "A real", false)]
        [InlineData("x.cs", "#define CWMonitor\n#if CWMonitor\nvar s = \"A real sentence under a switch its own file turns on.\";\n#endif", "A real", false)]
        [InlineData("x.cs", "#undef KEEP\n#if KEEP\nvar s = \"This text is under a symbol its own file has undefined, whatever a build defines.\";\n#endif", "This text", true)]
        [InlineData("x.vb", "' \"This text is quoted in a VB comment and nobody hears it at all.\"", "This text", true)]
        [InlineData("x.vb", "Dim s = \"A real VB sentence that the rule must keep on counting.\"", "A real", false)]
        public void The_filter_skips_only_what_is_never_heard(string file, string source, string target, bool expected)
        {
            int at = source.LastIndexOf(target, StringComparison.Ordinal) - 1;   // the opening quote
            Assert.True(at >= 0 && source[at] == '"', "fixture is malformed: no quote before the target");
            Assert.Equal(expected, NeverSpoken(file, source, at, new ParsedFile(), FixtureBuildText));
        }

        /// <summary>
        /// The never-defined judgement reads the real build: it must find the
        /// project files, and a name the build really uses must read as
        /// definable. Positive control for <see cref="BuildText"/>.
        /// </summary>
        [Fact]
        public void The_build_scan_reads_the_real_build_files()
        {
            string text = BuildText.Value;
            Assert.Contains("net10.0-windows", text);
            Assert.True(CouldBeDefinedByABuild("TargetFramework", text),
                "a word every project file contains was judged absent from the build, so the scan is not reading it");
            Assert.False(CouldBeDefinedByABuild("dontIgnore", text),
                "dontIgnore now appears in a build file; JJLogIO/logio/ScreenSaver.cs's #if dontIgnore block will be counted");
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
