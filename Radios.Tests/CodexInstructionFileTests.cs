using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// AGENTS.md is how Codex learns this project's rules, and it must never
    /// quietly stop saying what CLAUDE.md says.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two instruction files for one project is the setup this project's
    /// dominant defect loves.</b> Description drift has been found in
    /// CLAUDE.md itself four times; a second file restating its most
    /// dangerous rules doubles the surface. So AGENTS.md is a pointer, not a
    /// copy, and the handful of rules it DOES restate are held to CLAUDE.md
    /// by the second test here.
    /// </para>
    /// <para>
    /// <b>The size test exists because Codex fails silently.</b> It stops
    /// adding instruction text once the combined size reaches
    /// <c>project_doc_max_bytes</c>, 32 KiB by default — no warning, no
    /// error, the rest simply is not there. Measured 2026-09-16 on Codex CLI
    /// 0.154.0 with <c>codex debug prompt-input</c>: the whole of AGENTS.md
    /// arrived, and CLAUDE.md, at 93 KB, is nearly three times the limit,
    /// which is why it is read rather than loaded.
    /// </para>
    /// </remarks>
    public class CodexInstructionFileTests
    {
        private const string AgentsFile = "AGENTS.md";
        private const string ClaudeFile = "CLAUDE.md";

        /// <summary>Codex's default <c>project_doc_max_bytes</c>.</summary>
        private const int CodexInstructionLimitBytes = 32 * 1024;

        private const string BeginMarker = "<!-- hard-rules:begin -->";
        private const string EndMarker = "<!-- hard-rules:end -->";

        private static readonly Regex Bold = new(@"\*\*(.+?)\*\*", RegexOptions.Singleline);

        private static readonly Regex MemoryFolder = new(
            @"C:\\Users\\[^\\\s`]+\\\.claude\\projects\\[^\\\s`]+\\memory",
            RegexOptions.IgnoreCase);

        [Fact]
        public void AgentsMd_stays_well_under_what_Codex_will_read()
        {
            string path = IntegrationPassTree.At(AgentsFile);
            Assert.True(File.Exists(path),
                "AGENTS.md is missing, so Codex starts every session in this repository with "
                + "none of its rules. Codex does not read CLAUDE.md on its own — measured, not "
                + "assumed.");

            long bytes = new FileInfo(path).Length;

            // Three quarters of the limit, not the limit itself. Codex counts
            // its GLOBAL instruction file against the same budget, so a file
            // that fits exactly on its own is already too big on a machine
            // with any global guidance at all.
            long ceiling = CodexInstructionLimitBytes * 3 / 4;
            Assert.True(bytes <= ceiling,
                $"AGENTS.md is {bytes:N0} bytes against a working ceiling of {ceiling:N0}. "
                + $"Codex stops reading at {CodexInstructionLimitBytes:N0} and says nothing when "
                + "it does, so a long file loses its ending without anybody finding out. Move "
                + "detail into CLAUDE.md or the memory tree and point at it; do not raise the "
                + "limit in one machine's config and call it fixed.");
        }

        [Fact]
        public void Every_rule_AgentsMd_restates_is_still_a_rule_in_ClaudeMd()
        {
            string agents = File.ReadAllText(IntegrationPassTree.At(AgentsFile));
            string claude = Normalise(File.ReadAllText(IntegrationPassTree.At(ClaudeFile)));

            int begin = agents.IndexOf(BeginMarker, StringComparison.Ordinal);
            int end = agents.IndexOf(EndMarker, StringComparison.Ordinal);

            // POSITIVE CONTROL on the block. Deleting or misspelling a marker
            // must fail loudly — otherwise an emptied block extracts no
            // phrases, checks nothing, and passes.
            Assert.True(begin >= 0 && end > begin,
                "AGENTS.md has lost its hard-rules markers, so this check can no longer find "
                + "the rules it is meant to hold to CLAUDE.md.");

            string block = agents.Substring(begin, end - begin);
            string[] phrases = Bold.Matches(block)
                .Select(m => Normalise(m.Groups[1].Value))
                .Where(p => p.Length > 0)
                .ToArray();

            Assert.True(phrases.Length >= 8,
                $"only {phrases.Length} rule(s) were read out of AGENTS.md's hard-rules block. "
                + "Either the rules were cut, which Claude should decide deliberately, or the "
                + "bold markup that marks them was lost.");

            // NEGATIVE CONTROL on the matcher: a rule CLAUDE.md certainly
            // does not state must not be found. A comparison that always
            // matched would pass every phrase and prove nothing.
            Assert.DoesNotContain(
                Normalise("Always run `dotnet test` at solution scope"), claude,
                StringComparison.OrdinalIgnoreCase);

            string[] drifted = phrases
                .Where(p => !claude.Contains(p, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            Assert.True(drifted.Length == 0,
                "AGENTS.md restates rules that CLAUDE.md no longer contains:"
                + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", drifted)
                + Environment.NewLine
                + "One of the two files changed and the other did not. Codex reads AGENTS.md "
                + "first, so a rule that was reworded or retired in CLAUDE.md is still being "
                + "enforced on Codex in its old form — or a rule Codex is being told about no "
                + "longer has the explanation it points to. Fix whichever file is wrong.");
        }

        [Fact]
        public void AgentsMd_sends_Codex_to_the_memory_folder_ClaudeMd_uses()
        {
            string agents = File.ReadAllText(IntegrationPassTree.At(AgentsFile));
            string claude = File.ReadAllText(IntegrationPassTree.At(ClaudeFile));

            var inAgents = MemoryFolder.Matches(agents).Select(m => m.Value).Distinct(
                StringComparer.OrdinalIgnoreCase).ToArray();

            // POSITIVE CONTROL: the extractor must find the path at all.
            Assert.NotEmpty(inAgents);
            Assert.Matches(MemoryFolder, claude);

            foreach (string folder in inAgents)
            {
                Assert.True(claude.Contains(folder, StringComparison.OrdinalIgnoreCase),
                    $"AGENTS.md sends Codex to {folder}, which CLAUDE.md does not name. If the "
                    + "memory tree moved, Codex is now reading an empty or stale folder and "
                    + "believing it — which is worse than reading nothing.");
            }
        }

        /// <summary>
        /// Reduce a rule to its words, so formatting differences between the two
        /// files are not mistaken for drift.
        /// </summary>
        /// <remarks>
        /// Whitespace collapses, so a rule re-wrapped at another column is the same
        /// rule. Backticks go, because CLAUDE.md writes the desk-free variable as
        /// <c>JJFLEX_TIER1_DESK_FREE=1</c> in running text while AGENTS.md names it
        /// on its own. A trailing full stop goes, because a bold rule that ends a
        /// sentence and the same words as a heading are one rule. The first run of
        /// this test, on 2026-09-16, failed on exactly those three differences and
        /// on nothing else.
        /// </remarks>
        private static string Normalise(string text)
            => Regex.Replace(text.Replace("`", ""), @"\s+", " ").Trim().TrimEnd('.', ',', ';', ':');
    }
}
