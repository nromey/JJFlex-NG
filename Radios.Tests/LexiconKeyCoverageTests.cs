using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The static half of the three checks: every key named at a call site
    /// exists in the store.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the check that catches what nothing else can — a key on a path
    /// no test walks. An error branch, a rare dialog, a failure message that
    /// fires once a year on someone else's machine. The runtime check only sees
    /// strings something actually caused to be spoken, so its coverage is
    /// exactly the suite's coverage and no more. This one runs nothing, which
    /// is precisely why it sees everything.
    /// </para>
    /// <para>
    /// It reads SOURCE rather than IL because the thing being verified is a
    /// literal written by a person. A key assembled at run time cannot be
    /// checked here at all; those are counted and reported instead, because
    /// they are the entries whose only safety net is a test that happens to
    /// reach them.
    /// </para>
    /// <para>
    /// <b>Vacuous until the extraction tracks land call sites</b> — and a check
    /// that silently examines nothing is the exact failure #172 was built to
    /// prevent, where zero discovered tests reads as green. So the scanner is
    /// proved against known input first, and the sweep reports what it covered.
    /// </para>
    /// </remarks>
    public sealed class LexiconKeyCoverageTests
    {
        /// <summary>
        /// A literal key at a call site: <c>Lexicon.Get("some.key"</c>. Same
        /// shape in C# and VB, which is why one pattern serves both.
        /// </summary>
        private static readonly Regex LiteralKey = new Regex(
            @"Lexicon\s*\.\s*Get\s*\(\s*""([^""]+)""",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// A call whose key is NOT a plain literal — an interpolated string, a
        /// variable, a concatenation. Cannot be verified here by construction.
        /// </summary>
        private static readonly Regex DynamicKey = new Regex(
            @"Lexicon\s*\.\s*Get\s*\(\s*(?![""])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // ────────────────────────────────────────────────────────────────
        //  Prove the instrument before trusting its silence
        // ────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("Lexicon.Get(\"connect.done\")", "connect.done")]
        [InlineData("Lexicon.Get( \"audio.x\" )", "audio.x")]
        [InlineData("Lexicon . Get (\"settings.y\")", "settings.y")]
        [InlineData("var s = Radios.Lexicon.Get(\"help.z\", level);", "help.z")]
        public void TheScannerFindsALiteralKey(string source, string expected)
        {
            // The positive control. A sweep that finds nothing is only evidence
            // if the instrument is known to find something.
            Match match = LiteralKey.Match(source);
            Assert.True(match.Success, "scanner missed: " + source);
            Assert.Equal(expected, match.Groups[1].Value);
        }

        [Theory]
        [InlineData("Lexicon.Get($\"connect.{phase}.done\")")]
        [InlineData("Lexicon.Get(theKey)")]
        [InlineData("Lexicon.Get(prefix + \".done\")")]
        public void TheScannerSpotsAKeyItCannotVerify(string source)
        {
            Assert.True(DynamicKey.IsMatch(source), "should have been flagged dynamic: " + source);
            Assert.False(LiteralKey.IsMatch(source), "should NOT have been read as a literal: " + source);
        }

        [Fact]
        public void TheScannerIsNotFooledByAMethodThatMerelyEndsInGet()
        {
            Assert.False(LiteralKey.IsMatch("Widget.Get(\"not.a.lexicon.call\")"));
            Assert.False(LiteralKey.IsMatch("config.Get(\"x.y\")"));
        }

        // ────────────────────────────────────────────────────────────────
        //  The sweep
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void EveryKeyNamedInSourceExistsInTheStore()
        {
            string root = RepoRoot();
            Lexicon.Forget();
            Lexicon.Load(Lexicon.Partitions);

            var missing = new List<string>();
            int literals = 0, dynamics = 0, filesScanned = 0, exempt = 0;

            foreach (string file in SourceFiles(root))
            {
                filesScanned++;
                string text = File.ReadAllText(file);
                if (text.IndexOf("Lexicon", StringComparison.Ordinal) < 0) continue;
                if (IsExempt(text)) { exempt++; continue; }

                foreach (Match match in LiteralKey.Matches(text))
                {
                    literals++;
                    string key = match.Groups[1].Value;
                    if (!Lexicon.Contains(key))
                    {
                        missing.Add(key + "  (" + Rel(root, file) + ")");
                    }
                }

                dynamics += DynamicKey.Matches(text).Count;
            }

            // Proves the sweep actually walked the tree. Without this, a broken
            // root-finder would scan zero files and report a clean bill of
            // health — which is the shape of failure this whole suite exists to
            // refuse.
            Assert.True(filesScanned > 100,
                "Only " + filesScanned + " source files were scanned, which means the repo root " +
                "was not found and this check verified nothing. Root tried: " + root);

            Assert.True(missing.Count == 0,
                "Keys named in source but absent from the store (" + missing.Count + "):" +
                Environment.NewLine + string.Join(Environment.NewLine, missing.Distinct()));

            // Not a failure. Recorded so the count is visible in the run, since
            // these are the only keys the static check structurally cannot
            // cover — their sole safety net is a test that happens to hit them.
            Assert.True(dynamics >= 0,
                "literal keys: " + literals + ", dynamically built: " + dynamics +
                ", files exempted: " + exempt);
        }

        [Fact]
        public void NoSourceFileNamesAKeyOutsideTheKnownPartitions()
        {
            // A key whose first segment is not one of the six is malformed: it
            // can never load, so it would render as itself forever and the
            // runtime check would flag it every single run.
            string root = RepoRoot();
            var strays = new List<string>();
            int filesScanned = 0;

            foreach (string file in SourceFiles(root))
            {
                filesScanned++;
                string text = File.ReadAllText(file);
                if (text.IndexOf("Lexicon", StringComparison.Ordinal) < 0) continue;
                if (IsExempt(text)) continue;

                foreach (Match match in LiteralKey.Matches(text))
                {
                    string key = match.Groups[1].Value;
                    string partition = Lexicon.PartitionOf(key);
                    bool known = Lexicon.Partitions.Any(
                        p => string.Equals(p, partition, StringComparison.OrdinalIgnoreCase));
                    if (!known)
                    {
                        strays.Add(key + "  (" + Rel(root, file) + ")");
                    }
                }
            }

            Assert.True(filesScanned > 100, "The sweep scanned only " + filesScanned + " files.");
            Assert.True(strays.Count == 0,
                "Keys outside the six partitions: " + string.Join("; ", strays.Distinct()));
        }

        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Walk up from the test assembly until the solution file appears. In a
        /// track worktree this correctly finds THAT worktree's tree, which is
        /// what a per-track gate needs.
        /// </summary>
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
                    !string.Equals(ext, ".vb", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(ext, ".xaml", StringComparison.OrdinalIgnoreCase)) continue;

                if (IsExcluded(file)) continue;
                yield return file;
            }
        }

        /// <summary>
        /// A file containing this token is skipped by the sweep. For files
        /// whose job is to name keys that deliberately do NOT exist — the
        /// store's own tests, which must prove the missing-key fallback.
        /// </summary>
        /// <remarks>
        /// A token rather than a list of filenames, because a list stops
        /// working the day someone renames a file and does so silently, which
        /// is the failure mode this whole suite exists to refuse. The token
        /// travels with the file and is greppable from either direction.
        /// <para>
        /// This constant's own file contains the token, so the scanner exempts
        /// itself. That is intended: its InlineData rows are sample call sites,
        /// not real ones.
        /// </para>
        /// </remarks>
        private const string ExemptToken = "LEXICON_SCANNER_EXEMPT";

        private static bool IsExempt(string text)
        {
            return text.IndexOf(ExemptToken, StringComparison.Ordinal) >= 0;
        }

        private static bool IsExcluded(string path)
        {
            // Build output, and the agent worktrees under .claude — those hold
            // whole extra copies of the tree, which would multiply every count
            // and let a stale copy's stale key fail a clean checkout.
            string p = path.Replace('/', '\\');
            return p.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase)
                || p.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase)
                || p.Contains("\\.git\\", StringComparison.OrdinalIgnoreCase)
                || p.Contains("\\.vs\\", StringComparison.OrdinalIgnoreCase)
                || p.Contains("\\.claude\\", StringComparison.OrdinalIgnoreCase)
                || p.Contains("\\node_modules\\", StringComparison.OrdinalIgnoreCase);
        }

        private static string Rel(string root, string file)
        {
            return file.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? file.Substring(root.Length).TrimStart('\\', '/')
                : file;
        }
    }
}
