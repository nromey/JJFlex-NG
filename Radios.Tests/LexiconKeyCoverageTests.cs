using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
    /// literal written by a person.
    /// </para>
    /// </remarks>
    // LEXICON_SCANNER_EXEMPT — the sample call sites below are illustrations,
    // not real ones, so this file must not be swept for missing keys.
    //
    // Joined to the RadioConfig statics collection because this class calls
    // Lexicon.Forget() and Lexicon.Load(), and LexiconTests in that collection
    // mutates the same process-wide state. xUnit runs test CLASSES in parallel,
    // so without this the other class clears the store part-way through this
    // sweep and the failure surfaces here, far from its cause. That is exactly
    // what the collection's own doc comment warns about, and exactly what
    // happened on 2026-08-22 — intermittently, which is the worst way.
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class LexiconKeyCoverageTests
    {
        private static readonly Regex CallStart = new Regex(
            @"Lexicon\s*\.\s*Get\s*\(",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Every key a call site could pass, read from its FIRST argument only.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why a scanner and not a regex.</b> The first version of this check
        /// required the quote immediately after the open paren, so a key chosen
        /// by a ternary —
        /// <c>Lexicon.Get(failed ? "audio.tune.swr_failed" : "audio.tune.swr")</c>
        /// — read as dynamically built and was never verified, though both keys
        /// are plain literals sitting in the source. Track C found 26 such sites
        /// in its files alone on 2026-08-22, disproportionately on error and
        /// rare branches, which is exactly the coverage this check exists to
        /// provide. It was reporting every key verified while skipping the ones
        /// that mattered most.
        /// </para>
        /// <para>
        /// Widening the regex to "any quoted string in the call" would have been
        /// worse than the bug: <c>Lexicon.Get("connect.found", ("radio", name))</c>
        /// would harvest <c>radio</c> as a key and then fail on it. So this
        /// reads only as far as the first comma at paren depth zero, which is
        /// the key expression and nothing else.
        /// </para>
        /// <para>
        /// An interpolated string is counted as dynamic rather than collected —
        /// that genuinely is a key built at run time, and claiming to have
        /// checked it would be the same lie in a new coat.
        /// </para>
        /// </remarks>
        internal static List<string> KeysInFirstArgument(string text, int argStart, out bool anyDynamic)
        {
            var keys = new List<string>();
            anyDynamic = false;
            int depth = 0;
            int i = argStart;

            while (i < text.Length)
            {
                char c = text[i];

                if (c == '"')
                {
                    bool interpolated =
                        (i > 0 && text[i - 1] == '$') ||
                        (i > 1 && text[i - 1] == '@' && text[i - 2] == '$') ||
                        (i > 1 && text[i - 1] == '$' && text[i - 2] == '@');

                    var sb = new StringBuilder();
                    int end = i + 1;
                    while (end < text.Length && text[end] != '"')
                    {
                        if (text[end] == '\\' && end + 1 < text.Length) end++;
                        else sb.Append(text[end]);
                        end++;
                    }

                    if (interpolated) anyDynamic = true;
                    else keys.Add(sb.ToString());

                    i = end + 1;
                    continue;
                }

                if (c == '(') { depth++; i++; continue; }
                if (c == ')') { if (depth == 0) break; depth--; i++; continue; }
                if (c == ',' && depth == 0) break;

                // An identifier in the key position means a variable was
                // passed. Only word-initial letters count, so a ternary's own
                // punctuation does not trip it.
                if (char.IsLetter(c) && (i == argStart || !char.IsLetterOrDigit(text[i - 1])))
                {
                    int w = i;
                    while (w < text.Length &&
                           (char.IsLetterOrDigit(text[w]) || text[w] == '_' || text[w] == '.')) w++;
                    anyDynamic = true;
                    i = w;
                    continue;
                }

                i++;
            }

            return keys;
        }

        /// <summary>Every key named anywhere in one file, plus a dynamic tally.</summary>
        private static List<string> AllKeysIn(string text, out int dynamicCalls)
        {
            var keys = new List<string>();
            dynamicCalls = 0;
            foreach (Match m in CallStart.Matches(text))
            {
                var found = KeysInFirstArgument(text, m.Index + m.Length, out bool dyn);
                if (found.Count == 0 || dyn) dynamicCalls++;
                keys.AddRange(found);
            }
            return keys;
        }

        // ────────────────────────────────────────────────────────────────
        //  Prove the instrument before trusting its silence
        // ────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("Lexicon.Get(\"connect.done\")", "connect.done")]
        [InlineData("Lexicon.Get( \"audio.x\" )", "audio.x")]
        [InlineData("Lexicon . Get (\"settings.y\")", "settings.y")]
        [InlineData("var s = Radios.Lexicon.Get(\"help.z\");", "help.z")]
        public void TheScannerFindsALiteralKey(string source, string expected)
        {
            Assert.Contains(expected, AllKeysIn(source, out _));
        }

        [Fact]
        public void TheScannerFindsBOTHKeysOfATernary()
        {
            // The regression that motivated replacing the regex. Track C had 26
            // of these on 2026-08-22 and every one was silently unchecked.
            var keys = AllKeysIn(
                "Lexicon.Get(failed ? \"audio.tune.swr_failed\" : \"audio.tune.swr\")", out _);

            Assert.Contains("audio.tune.swr_failed", keys);
            Assert.Contains("audio.tune.swr", keys);
        }

        [Fact]
        public void TheScannerReadsOnlyTheKeyAndNotThePlaceholderNames()
        {
            // Widening the old regex would have harvested "radio" as a key and
            // then failed on it. Only the first argument is the key.
            var keys = AllKeysIn("Lexicon.Get(\"connect.found\", (\"radio\", name))", out _);

            Assert.Single(keys);
            Assert.Equal("connect.found", keys[0]);
        }

        [Fact]
        public void TheScannerReadsOnlyTheKeyWhenAVerbosityArgumentFollows()
        {
            var keys = AllKeysIn(
                "Lexicon.Get(\"connect.bye\", VerbosityLevel.Terse, (\"radio\", n))", out _);

            Assert.Single(keys);
            Assert.Equal("connect.bye", keys[0]);
        }

        [Theory]
        [InlineData("Lexicon.Get($\"connect.{phase}.done\")")]
        [InlineData("Lexicon.Get(theKey)")]
        [InlineData("Lexicon.Get(prefix + \".done\")")]
        public void TheScannerStillReportsAKeyItCannotVerify(string source)
        {
            AllKeysIn(source, out int dyn);
            Assert.True(dyn > 0, "should have been counted as dynamic: " + source);
        }

        [Fact]
        public void TheScannerIsNotFooledByAMethodThatMerelyEndsInGet()
        {
            Assert.Empty(AllKeysIn("Widget.Get(\"not.a.lexicon.call\")", out _));
            Assert.Empty(AllKeysIn("config.Get(\"x.y\")", out _));
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

                List<string> keys = AllKeysIn(text, out int dyn);
                dynamics += dyn;
                foreach (string key in keys)
                {
                    literals++;
                    if (!Lexicon.Contains(key)) missing.Add(key + "  (" + Rel(root, file) + ")");
                }
            }

            // Proves the sweep walked the tree. A broken root-finder would scan
            // zero files and report a clean bill of health, which is the shape
            // of failure this whole suite exists to refuse.
            Assert.True(filesScanned > 100,
                "Only " + filesScanned + " source files were scanned, which means the repo root " +
                "was not found and this check verified nothing. Root tried: " + root);

            Assert.True(missing.Count == 0,
                "Keys named in source but absent from the store (" + missing.Count + "):" +
                Environment.NewLine + string.Join(Environment.NewLine, missing.Distinct()));

            Assert.True(dynamics >= 0,
                "literal keys: " + literals + ", dynamically built: " + dynamics +
                ", files exempted: " + exempt);
        }

        [Fact]
        public void NoSourceFileNamesAKeyOutsideTheKnownPartitions()
        {
            string root = RepoRoot();
            var strays = new List<string>();
            int filesScanned = 0;

            foreach (string file in SourceFiles(root))
            {
                filesScanned++;
                string text = File.ReadAllText(file);
                if (text.IndexOf("Lexicon", StringComparison.Ordinal) < 0) continue;
                if (IsExempt(text)) continue;

                foreach (string key in AllKeysIn(text, out _))
                {
                    string partition = Lexicon.PartitionOf(key);
                    bool known = Lexicon.Partitions.Any(
                        p => string.Equals(p, partition, StringComparison.OrdinalIgnoreCase));
                    if (!known) strays.Add(key + "  (" + Rel(root, file) + ")");
                }
            }

            Assert.True(filesScanned > 100, "The sweep scanned only " + filesScanned + " files.");
            Assert.True(strays.Count == 0,
                "Keys outside the six partitions: " + string.Join("; ", strays.Distinct()));
        }

        // ────────────────────────────────────────────────────────────────

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
        /// A file containing this token is skipped by the sweep. For files whose
        /// job is to name keys that deliberately do NOT exist — the store's own
        /// tests, which must prove the missing-key fallback.
        /// </summary>
        /// <remarks>
        /// A token rather than a filename list, because a list stops working the
        /// day someone renames a file and does so silently. This file carries
        /// the token itself: its sample call sites are illustrations, not real
        /// ones.
        /// </remarks>
        private const string ExemptToken = "LEXICON_SCANNER_EXEMPT";

        private static bool IsExempt(string text)
        {
            return text.IndexOf(ExemptToken, StringComparison.Ordinal) >= 0;
        }

        private static bool IsExcluded(string path)
        {
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
