using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// No two targets in one value sub-layer may claim the same selection key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>#539 is why this exists, and the shape of that bug is the argument for
    /// a test rather than care.</b> Noel's letter ruling of 2026-09-02 moved PC
    /// output to <c>O</c> so that plain <c>P</c> could go back to pan. The pan
    /// half landed; the PC-output half did not. Both targets therefore carried
    /// <c>Keys.P</c>, and <see cref="ValueSubLayer"/> selects with
    /// <c>FirstOrDefault</c> - so the earlier registration simply won and the
    /// later one became unreachable.
    /// </para>
    /// <para>
    /// <b>Nothing failed.</b> It compiled, every test passed, both keys were
    /// bound, and pressing <c>P</c> did something plausible. The layer's own
    /// prompt string, both help pages and the changelog all said <c>O</c> was
    /// PC output and <c>P</c> was pan - so the documentation was right, the
    /// code was wrong, and the only symptom was a letter that did nothing and
    /// another that did the wrong thing quietly. It shipped in a build the
    /// operator tested for three days without either of us noticing.
    /// </para>
    /// <para>
    /// <b>The existing suite could not see it</b> because
    /// <c>ValueSubLayerTests</c> builds its own fake layer, and that fake used
    /// <c>Keys.O</c> - the letter the real code was supposed to have. A test
    /// double that models the INTENDED design cannot catch the shipped one
    /// diverging from it, which is exactly the gap a source scan closes.
    /// </para>
    /// <para>
    /// This reads the real file rather than constructing a layer because the
    /// layers are built in <c>JJFlexWpf</c>, which <c>Radios.Tests</c> does not
    /// reference. Same reasoning and same idiom as
    /// <c>LeaderUnknownKeyTests</c> and <c>WarningDuckScopeTests</c>.
    /// </para>
    /// </remarks>
    public sealed class ValueLayerSelectKeyUniquenessTests
    {
        private const string KeyCommandsFile = "JJFlexWpf/KeyCommands.cs";

        // A Level(...) helper call passes its key third; a raw ValueTarget sets
        // SelectKey. Both forms appear in the same layer, so both are read.
        private static readonly Regex LevelCall = new Regex(
            @"Level\(\s*""(?<id>[a-z0-9-]+)""\s*,\s*""[^""]*""\s*,\s*(?<key>Keys\.[A-Za-z0-9_]+(?:\s*\|\s*Keys\.[A-Za-z0-9_]+)*)",
            RegexOptions.Compiled);

        private static readonly Regex SelectKeyAssign = new Regex(
            @"Id\s*=\s*""(?<id>[a-z0-9-]+)""(?<between>[\s\S]{0,900}?)SelectKey\s*=\s*(?<key>Keys\.[A-Za-z0-9_]+(?:\s*\|\s*Keys\.[A-Za-z0-9_]+)*)",
            RegexOptions.Compiled);

        [Fact]
        public void No_two_targets_in_the_audio_layer_claim_the_same_letter()
        {
            AssertUniqueWithin("internal void EnterAudioLayer");
        }

        [Fact]
        public void No_two_targets_in_the_filter_layer_claim_the_same_letter()
        {
            AssertUniqueWithin("internal void EnterFilterLayer");
        }

        /// <summary>
        /// Positive control. The scanner must FIND keys, or an empty result
        /// would pass every assertion above while measuring nothing - the
        /// failure mode this whole file exists to close.
        /// </summary>
        [Fact]
        public void The_scanner_actually_finds_the_audio_layer_keys()
        {
            var found = KeysIn(BodyOf("internal void EnterAudioLayer"));

            Assert.True(found.Count >= 6,
                "expected at least six selectable targets in the audio layer, found "
                + found.Count + ": " + Describe(found));

            // The two letters #539 was about, named explicitly so a future
            // rename cannot quietly empty this control.
            Assert.Contains(found, f => f.Id == "pc-output");
            Assert.Contains(found, f => f.Id == "pan");
        }

        /// <summary>
        /// The regression itself, pinned by name: these two must not agree.
        /// </summary>
        [Fact]
        public void Pc_output_and_pan_do_not_share_a_key()
        {
            var found = KeysIn(BodyOf("internal void EnterAudioLayer"));
            string pcOutput = found.Single(f => f.Id == "pc-output").Key;
            string pan = found.Single(f => f.Id == "pan").Key;

            Assert.False(pcOutput == pan,
                "pc-output and pan both select on " + pcOutput
                + ". ValueSubLayer picks with FirstOrDefault, so the later one is unreachable. See #539.");
        }

        private static void AssertUniqueWithin(string methodName)
        {
            var found = KeysIn(BodyOf(methodName));

            var clashes = found
                .GroupBy(f => f.Key, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .ToList();

            Assert.True(clashes.Count == 0,
                methodName + " has targets sharing a selection key. ValueSubLayer "
                + "selects with FirstOrDefault, so every target after the first is "
                + "unreachable by its own letter and nothing fails: "
                + string.Join("; ", clashes.Select(g =>
                      g.Key + " claimed by " + string.Join(" and ", g.Select(x => x.Id)))));
        }

        private static List<(string Id, string Key)> KeysIn(string body)
        {
            var found = new List<(string, string)>();

            foreach (Match m in LevelCall.Matches(body))
                found.Add((m.Groups["id"].Value, Normalise(m.Groups["key"].Value)));

            foreach (Match m in SelectKeyAssign.Matches(body))
            {
                // Guard against the regex spanning from one target's Id into a
                // later target's SelectKey: a nested Id in between means the
                // two do not belong together.
                if (m.Groups["between"].Value.Contains("Id = \"", StringComparison.Ordinal)) continue;
                found.Add((m.Groups["id"].Value, Normalise(m.Groups["key"].Value)));
            }

            return found;
        }

        private static string Normalise(string key) =>
            string.Join(" | ",
                key.Split('|').Select(p => p.Trim()).OrderBy(p => p, StringComparer.Ordinal));

        private static string Describe(IEnumerable<(string Id, string Key)> found) =>
            string.Join(", ", found.Select(f => f.Id + "=" + f.Key));

        private static string BodyOf(string methodName)
        {
            string source = Source(KeyCommandsFile);
            int at = source.IndexOf(methodName, StringComparison.Ordinal);
            Assert.True(at >= 0, methodName + " not found in " + KeyCommandsFile);

            int open = source.IndexOf('{', at);
            Assert.True(open >= 0, "no body for " + methodName);

            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0) return source.Substring(open, i - open + 1);
                }
            }

            Assert.Fail("unbalanced braces in " + methodName);
            return "";
        }

        private static string Source(string relative)
        {
            string path = Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), "source not found: " + path);
            return File.ReadAllText(path);
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
    }
}
