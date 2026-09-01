using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The Tools items that drive <c>ProfileReporter</c> must be tellable apart
    /// by ear, from their first word.
    /// </summary>
    /// <remarks>
    /// <para>
    /// #420. There are three of them and they used to be called "Profile
    /// Report", "Export Settings as Text" and "Export Settings for Restore".
    /// Both exports write text and both prepare for a restore, so neither name
    /// named the difference — and two of the three load every stored profile
    /// on the radio in turn, moving the station for a minute or two in front of
    /// every connected client, which no name mentioned at all.
    /// </para>
    /// <para>
    /// A menu is arrowed through and heard. Two items that agree for two words
    /// are two items an operator has to wait out before they can choose, and
    /// the one they are waiting to identify is the one that reconfigures their
    /// radio. So this pins the property, not the prose: <b>no two of these
    /// items may open with the same word.</b> Noel rules the wording; any
    /// wording that respects the design passes.
    /// </para>
    /// <para>
    /// Harvested from source rather than from a live menu because building the
    /// real one needs a window, and this suite must never put a window on the
    /// operator's desktop.
    /// </para>
    /// </remarks>
    public sealed class ExportMenuNamesDivergeByEarTests
    {
        private const string MenuBar = "JJFlexWpf/NativeMenuBar.cs";

        [Fact]
        public void TheProfileDrivingToolsItemsDivergeOnTheirFirstWord()
        {
            var labels = ProfileDrivingItemLabels();

            // Positive control in the same breath as the assertion: if the
            // harvest silently stopped matching, an empty set would pass the
            // distinctness check below without inspecting anything.
            Assert.True(labels.Count >= 3,
                "expected at least three Tools items driving ProfileReporter (the "
                + "comparison report and the two exports) and harvested " + labels.Count
                + ": " + string.Join(" | ", labels) + ". Either items were removed, or "
                + "the AddWired call shape changed and this harvest needs to follow it.");

            var byFirstWord = labels.GroupBy(FirstWord, StringComparer.OrdinalIgnoreCase)
                                    .Where(g => g.Count() > 1)
                                    .ToList();

            Assert.True(byFirstWord.Count == 0,
                "these Tools items open with the same word, so an operator arrowing the "
                + "menu cannot tell them apart until several words in — and the one they "
                + "are waiting to identify is the one that loads every profile on the "
                + "radio (#420): "
                + string.Join("; ", byFirstWord.Select(
                    g => "\"" + g.Key + "\" -> " + string.Join(" and ", g))));
        }

        [Fact]
        public void TheHarvestFindsLabelsItIsKnownToContain()
        {
            // The negative result above is only worth something if the reader
            // demonstrably sees real text. Prove it on a line that is in the
            // file for an unrelated reason.
            string src = Read(MenuBar);
            Assert.Contains("AddWired(tools,", src, StringComparison.Ordinal);
            Assert.Contains("ProfileReporter.", src, StringComparison.Ordinal);
            Assert.NotEmpty(ProfileDrivingItemLabels());
        }

        // ── Plumbing ──

        /// <summary>
        /// Every <c>AddWired(tools, "…")</c> label whose handler body — bounded
        /// at the next item, so no label inherits its neighbour's evidence —
        /// calls into <c>ProfileReporter</c>.
        /// </summary>
        private static List<string> ProfileDrivingItemLabels()
        {
            string src = Read(MenuBar);
            var starts = Regex.Matches(src, @"Add(?:Wired|Command|Checked)\s*\(\s*tools\s*,\s*""([^""]*)""")
                              .Cast<Match>()
                              .ToList();

            var labels = new List<string>();
            for (int i = 0; i < starts.Count; i++)
            {
                int from = starts[i].Index + starts[i].Length;
                int to = i + 1 < starts.Count ? starts[i + 1].Index : src.Length;
                string body = src.Substring(from, to - from);
                if (body.Contains("ProfileReporter.", StringComparison.Ordinal))
                    labels.Add(starts[i].Groups[1].Value);
            }
            return labels;
        }

        private static string FirstWord(string label)
        {
            // Menu labels can carry an accelerator after a tab; the operator
            // hears the text before it.
            string text = label.Split('\t')[0].TrimStart();
            return new string(text.TakeWhile(c => char.IsLetterOrDigit(c)).ToArray())
                .ToLowerInvariant();
        }

        private static string Read(string relative)
        {
            string path = Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path),
                "Could not find " + relative + " (looked at " + path + "). A test that "
                + "cannot find its subject proves nothing about it.");
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
