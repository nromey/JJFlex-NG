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
    /// The documents that tell an agent what to do must not name things that do
    /// not exist.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A check that can only ever pass is not a check.</b> CLAUDE.md's
    /// keyboard audit says to grep the sprint's diff for <c>RegisterScope</c>.
    /// No source file in this repository has ever contained that word — the
    /// enum is <c>KeyScope</c>. A grep for a name that does not exist returns
    /// nothing, and nothing reads exactly like "no key bindings changed", so
    /// the audit reports clean and stops. Every keyboard audit run against that
    /// instruction has been vacuous.
    /// </para>
    /// <para>
    /// <b>Only documents that INSTRUCT are swept.</b> CLAUDE.md is the project
    /// contract and MIGRATION.md is a procedure; both tell a reader to go and
    /// look at something. README, the changelog, the principles and the TODO
    /// describe rather than direct, and a description may legitimately name
    /// something that has since gone. The distinction is not stylistic: an
    /// instruction naming a phantom silently produces a wrong ACTION, and that
    /// is the failure being defended against.
    /// </para>
    /// <para>
    /// <b>Paragraphs that read as history are exempt.</b> These documents
    /// deliberately record what they used to say — "this said
    /// <c>KeyCommands.vb</c> until 2026-08-21 and no such file exists" is the
    /// correction, not the drift, and nagging about it would train a reader to
    /// ignore the whole check. Same judgement <c>check-memory-drift.ps1</c>
    /// makes, applied per paragraph rather than per file because CLAUDE.md
    /// carries live instruction and its own errata side by side.
    /// </para>
    /// <para>
    /// <b>How this differs from <c>check-memory-drift.ps1</c>, which already
    /// exists.</b> That script sweeps the memory directory and matches FILE
    /// PATHS only — its header says "file paths and code symbols" but there is
    /// no symbol extraction in it, which is itself a small piece of description
    /// drift. This sweeps the repository's own instruction documents, which
    /// that script never opens, and it matches symbols as well as paths. The
    /// two do not overlap.
    /// </para>
    /// </remarks>
    public class IntegrationPassInstructionTests
    {
        /// <summary>
        /// Documents that tell a reader to do something. Each must exist — a
        /// missing instruction document is drift of its own, and silently
        /// skipping it would shrink the sweep without saying so.
        /// </summary>
        private static readonly string[] InstructionDocs =
        {
            "CLAUDE.md",
            "MIGRATION.md",
        };

        /// <summary>
        /// Words that make a paragraph a record rather than a direction.
        /// Generous on purpose: a false alarm costs the check its credibility,
        /// and the finding this rule exists for survives every one of these.
        /// </summary>
        private static readonly Regex ReadsAsHistory = new(
            @"\b(was|were|used to|until|no longer|formerly|previously|deleted|removed|"
            + @"retired|renamed|superseded|replaced|stale|obsolete|legacy|does not exist|"
            + @"no such|neither|nor|wrong|old|before|instead of)\b",
            RegexOptions.IgnoreCase);

        private static readonly Regex Backticked = new(@"`([^`\n]+)`");
        private static readonly Regex Identifier = new(@"^[A-Za-z_][A-Za-z0-9_]*$");
        private static readonly Regex PathLike =
            new(@"\.(cs|vb|xaml|ps1|bat|nsi|sln|csproj|vbproj)$", RegexOptions.IgnoreCase);

        [Fact]
        public void No_instruction_names_a_symbol_the_tree_does_not_contain()
        {
            var findings = new List<Finding>();
            int checkedRefs = 0;

            IReadOnlySet<string> symbols = EveryIdentifierInTheTree();

            // POSITIVE CONTROL. A corpus reader that returned nothing would
            // report every backticked word in CLAUDE.md as a phantom, which is
            // loud; one that returned everything would report none, which is
            // silent. Prove it holds a name that is certainly there and lacks
            // one that is certainly not.
            Assert.Contains("FixerPage", symbols);
            Assert.Contains("KeyScope", symbols);
            Assert.DoesNotContain("RegisterScope", symbols);

            foreach (string doc in InstructionDocs)
            {
                string path = IntegrationPassTree.At(doc);
                Assert.True(File.Exists(path),
                    doc + " is named as an instruction document and is not in the tree. Either it "
                    + "moved, in which case this list is stale, or it went, in which case whatever "
                    + "sent a reader to it is stale.");

                foreach (string paragraph in Regex.Split(File.ReadAllText(path), @"\r?\n\s*\r?\n"))
                {
                    bool history = ReadsAsHistory.IsMatch(paragraph);

                    foreach (Match m in Backticked.Matches(paragraph))
                    {
                        string span = m.Groups[1].Value.Trim();
                        if (!LooksLikeASymbol(span)) continue;

                        // Counted whether or not the paragraph is exempt. The
                        // control below asks whether the EXTRACTOR still works;
                        // counting only what survives the history filter would
                        // make a filter that swallowed everything look like a
                        // document with nothing in it.
                        checkedRefs++;
                        if (history) continue;
                        if (symbols.Contains(span)) continue;

                        findings.Add(new Finding(Rules.PhantomSymbol, doc + "/" + span,
                            doc + " names `" + span + "` as something to look for, and no file in "
                            + "the tree contains that token. An instruction to search for a name "
                            + "nobody wrote returns nothing, and nothing is indistinguishable "
                            + "from a clean result."));
                    }
                }
            }

            Assert.True(checkedRefs > 30,
                "only " + checkedRefs + " symbol reference(s) were checked across "
                + string.Join(" and ", InstructionDocs) + ", so either the documents have been "
                + "gutted or the extractor has stopped recognising them.");

            Gate(Rules.PhantomSymbol,
                 "An instruction that tells you to go and find something must name something "
                 + "findable. A grep for a word nobody ever wrote returns silence, and silence "
                 + "reads as \"nothing to see here\".",
                 findings);
        }

        [Fact]
        public void No_instruction_names_a_file_the_tree_does_not_contain()
        {
            var findings = new List<Finding>();
            int checkedRefs = 0;

            foreach (string doc in InstructionDocs)
            {
                string path = IntegrationPassTree.At(doc);
                Assert.True(File.Exists(path), doc + " is not in the tree.");

                foreach (string paragraph in Regex.Split(File.ReadAllText(path), @"\r?\n\s*\r?\n"))
                {
                    bool history = ReadsAsHistory.IsMatch(paragraph);

                    foreach (Match m in Backticked.Matches(paragraph))
                    {
                        string span = m.Groups[1].Value.Trim();
                        if (!PathLike.IsMatch(span)) continue;
                        if (span.Contains('*') || span.Contains('<') || span.Contains('>')) continue;

                        string leaf = Path.GetFileName(span.Replace('/', '\\'));
                        if (leaf.Length == 0 || leaf.StartsWith('.')) continue;

                        checkedRefs++;
                        if (history) continue;
                        if (IntegrationPassTree.FileNames.Contains(leaf)) continue;

                        findings.Add(new Finding(Rules.PhantomPath, doc + "/" + span,
                            doc + " names `" + span + "` and no file of that name is in the tree."));
                    }
                }
            }

            // POSITIVE CONTROL on the index rather than on the extractor: a
            // FileNames set that had everything in it would never report a
            // miss, and one that had nothing would report every reference.
            Assert.Contains("CLAUDE.md", IntegrationPassTree.FileNames);
            Assert.DoesNotContain("KeyCommands.vb", IntegrationPassTree.FileNames);

            Assert.True(checkedRefs > 15,
                "only " + checkedRefs + " file reference(s) were checked, so the extractor has "
                + "stopped recognising the paths these documents are full of.");

            Gate(Rules.PhantomPath,
                 "A document that sends a reader to a file must send them to one that is there.",
                 findings);
        }

        /// <summary>
        /// Which backticked spans are worth checking as symbols.
        /// </summary>
        /// <remarks>
        /// Narrow on purpose. Backticks in these documents also wrap shell
        /// commands, flags, branch names, settings values and ordinary
        /// emphasis; measured against the real documents, requiring a bare
        /// identifier with an internal capital and at least five characters
        /// left 162 references to check and two findings, both real. A wider
        /// net would have found the same two under a pile of noise, and a
        /// finding nobody reads is not a finding.
        /// </remarks>
        private static bool LooksLikeASymbol(string span)
            => Identifier.IsMatch(span)
            && span.Length >= 5
            && span.Any(char.IsUpper)
            && !span.All(c => char.IsUpper(c) || c == '_');   // SHOUTED words are prose

        /// <summary>
        /// A file containing this token is left out of the identifier corpus.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Because this check found itself.</b> The baseline records that
        /// CLAUDE.md names <c>RegisterScope</c>, so the baseline contains the
        /// word <c>RegisterScope</c>, so the sweep read it back out of the tree
        /// and concluded the symbol exists. The finding erased itself by being
        /// written down — and it would have gone on doing that silently, with a
        /// green test, for every phantom anybody ever recorded here.
        /// </para>
        /// <para>
        /// A token rather than a list of file names, following
        /// <c>LexiconKeyCoverageTests</c>: a list stops working the day
        /// somebody renames a file, and does so quietly. The cost is that these
        /// files' own identifiers are not evidence either, which is right —
        /// nothing in CLAUDE.md should be pointing at them.
        /// </para>
        /// </remarks>
        private const string CorpusExemptToken = "INTEGRATION_PASS_CORPUS_EXEMPT";

        /// <summary>
        /// Every identifier token in the tree, vendor and project files
        /// included.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Wider than C# on purpose. An instruction naming
        /// <c>RootNamespace</c> or <c>cleanupPeriodDays</c> is naming something
        /// real; it lives in a project file or a JSON settings file. Scoping
        /// this to source would report both as phantoms and teach the reader
        /// that the check cries wolf.
        /// </para>
        /// <para>
        /// Token-exact, not substring. That is a deliberate choice about which
        /// error to prefer: `KeyBinding` does appear inside
        /// <c>ValidateKeyBindings</c>, so a grep for it returns hits and the
        /// audit does not silently pass — but this tree declares no XAML input
        /// binding anywhere, so the sentence is still describing a mechanism
        /// that is not here. Reporting it lets a person decide what it meant to
        /// say. Substring matching would hide it.
        /// </para>
        /// </remarks>
        private static IReadOnlySet<string> EveryIdentifierInTheTree()
        {
            var tokens = new HashSet<string>(StringComparer.Ordinal);
            var word = new Regex(@"[A-Za-z_][A-Za-z0-9_]*");

            foreach (string file in IntegrationPassTree.AllFiles)
            {
                string text = IntegrationPassTree.Read(file);
                if (text.Contains(CorpusExemptToken, StringComparison.Ordinal)) continue;
                foreach (Match m in word.Matches(text))
                    tokens.Add(m.Value);
            }

            Assert.True(tokens.Count > 10_000,
                "only " + tokens.Count + " identifiers were read out of the tree, which is far "
                + "too few — the corpus reader has broken and everything would look like a "
                + "phantom.");
            return tokens;
        }
    }
}
