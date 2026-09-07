using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Noel's ruling of 2026-09-02, held in place: what the app says while it
    /// looks for radios is "Searching for radios", and nothing chattier.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a source scan and not a lexicon check.</b> The ruling was applied
    /// in Sprint 44 to <c>connect.json</c> and to the search window's caption,
    /// and a grep for the fixed copies confirmed it. Three spoken lines kept
    /// the old wording anyway (#551), because they were string literals in
    /// <c>ApplicationEvents.vb</c> and <c>globals.vb</c> — VB, hardcoded,
    /// invisible to anyone auditing the lexicon. The defect is precisely that
    /// spoken words lived outside the store, so the check has to read the
    /// source, in both languages, and refuse the literal wherever it is.
    /// </para>
    /// <para>
    /// Comments are stripped before matching, because the history of the old
    /// wording is worth keeping in comments and worthless in speech. Test
    /// projects are skipped: the arbiter tests quote the 2026-09-01 capture as
    /// fixtures, and a fixture is a record, not an announcement.
    /// </para>
    /// </remarks>
    // In the RadioConfig statics collection because TheLexicon_CarriesTheRuledWording
    // calls Lexicon.Forget(), and LexiconTests in that collection mutates the
    // same process-wide store; xUnit runs classes in parallel otherwise.
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class DiscoveryWordingTests
    {
        /// <summary>The phrases the ruling retired, as they would sit inside a string literal.</summary>
        private static readonly Regex RetiredInsideQuotes = new Regex(
            "\"[^\"\\r\\n]*(looking for radios|discovering radios|still looking)[^\"\\r\\n]*\"",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        [Fact]
        public void NoSourceFile_SpeaksTheRetiredDiscoveryWording()
        {
            string root = RepoRoot();
            var offenders = new List<string>();
            int filesScanned = 0;

            foreach (string file in SourceFiles(root))
            {
                filesScanned++;
                string[] lines = File.ReadAllLines(file);
                bool vb = file.EndsWith(".vb", StringComparison.OrdinalIgnoreCase);
                for (int i = 0; i < lines.Length; i++)
                {
                    string code = StripComment(lines[i], vb);
                    if (code.Length == 0) continue;
                    if (RetiredInsideQuotes.IsMatch(code))
                        offenders.Add(Rel(root, file) + ":" + (i + 1) + "  " + code.Trim());
                }
            }

            Assert.True(filesScanned > 100, "The sweep scanned only " + filesScanned + " files.");
            Assert.True(offenders.Count == 0,
                "Spoken discovery wording outside the lexicon, against the 2026-09-02 ruling "
                + "(say \"Searching for radios\"): " + Environment.NewLine
                + string.Join(Environment.NewLine, offenders));
        }

        [Fact]
        public void TheLexicon_CarriesTheRuledWording()
        {
            // The two lines the progress voice speaks while discovery runs.
            // Both call sites reach them through these keys, so if either key
            // vanished the operator would hear the key name read out.
            Lexicon.Forget();
            Assert.Equal("Searching for radios.", Lexicon.Get("connect.discovery.searching"));
            Assert.Equal("Still searching.", Lexicon.Get("connect.discovery.still_searching"));
        }

        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Drop a line comment. C# and VB both quote the old wording in comments
        /// as history, and history is allowed; only code that could SPEAK it is
        /// not. VB comments are stripped only at the start of the line, because
        /// an apostrophe mid-line is far more often inside a string than a
        /// comment marker.
        /// </summary>
        private static string StripComment(string line, bool vb)
        {
            if (vb)
            {
                return line.TrimStart().StartsWith("'", StringComparison.Ordinal) ? "" : line;
            }
            int slash = line.IndexOf("//", StringComparison.Ordinal);
            return slash >= 0 ? line.Substring(0, slash) : line;
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            return AppContext.BaseDirectory;
        }

        private static IEnumerable<string> SourceFiles(string root)
        {
            foreach (string file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(file);
                if (!string.Equals(ext, ".cs", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(ext, ".vb", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsExcluded(file)) continue;
                yield return file;
            }
        }

        private static bool IsExcluded(string path)
        {
            string p = path.Replace('/', '\\');
            return p.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase)
                || p.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase)
                || p.Contains("\\.git\\", StringComparison.OrdinalIgnoreCase)
                || p.Contains("\\.vs\\", StringComparison.OrdinalIgnoreCase)
                || p.Contains("\\.claude\\", StringComparison.OrdinalIgnoreCase)
                || p.Contains("\\node_modules\\", StringComparison.OrdinalIgnoreCase)
                || p.Contains(".Tests\\", StringComparison.OrdinalIgnoreCase)
                || p.Contains("\\FlexLib_API\\", StringComparison.OrdinalIgnoreCase);
        }

        private static string Rel(string root, string file)
        {
            return file.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? file.Substring(root.Length).TrimStart('\\', '/')
                : file;
        }
    }
}
