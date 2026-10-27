using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The one way a test finds a help page: by its bare name, wherever the
    /// outline has put it (#826, plan 4.11).
    /// </summary>
    /// <remarks>
    /// Tests used to pin a full path, <c>docs/help/md/leader-key.md</c>, so the
    /// 2026-10-27 move into folders broke thirteen of them without one page
    /// changing a word. A bare name survives any later move between folders,
    /// for the same reason page-to-page links in the help are bare names: the
    /// build guarantees each name is unique across the tree, ignoring case.
    /// So the lookup fails on zero matches AND on two, and on a name whose case
    /// differs from the file's, rather than guessing.
    /// </remarks>
    internal static class HelpPages
    {
        internal static string HelpRoot => Path.Combine(LeaderSourceScan.RepoRoot(), "docs", "help");

        internal static string MdRoot => Path.Combine(HelpRoot, "md");

        internal static string OutlinePath => Path.Combine(HelpRoot, "outline.md");

        /// <summary>Every page file under md/, in every folder.</summary>
        internal static IReadOnlyList<string> AllPageFiles() =>
            Directory.EnumerateFiles(MdRoot, "*.md", SearchOption.AllDirectories).ToList();

        /// <summary>The full path of the page with this bare name, e.g. "JJKey.md".</summary>
        internal static string PathOf(string bareName)
        {
            Assert.False(bareName.Contains('/') || bareName.Contains('\\'),
                "HelpPages takes a bare page name like \"JJKey.md\", not a path: " + bareName);

            var hits = AllPageFiles()
                .Where(f => string.Equals(Path.GetFileName(f), bareName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            Assert.True(hits.Count == 1,
                hits.Count == 0
                    ? "No help page is named " + bareName + " anywhere under " + MdRoot
                      + ". If it was renamed, the outline (docs/help/outline.md) shows its new name."
                    : "Two help pages are named " + bareName + ": " + string.Join(", ", hits)
                      + ". Page names must be unique across every folder; the help build refuses this too.");

            Assert.True(Path.GetFileName(hits[0]) == bareName,
                "The help page is " + Path.GetFileName(hits[0]) + ", not " + bareName + " (case differs).");
            return hits[0];
        }

        internal static string Read(string bareName) => File.ReadAllText(PathOf(bareName));

        /// <summary>The path relative to md/, with forward slashes, e.g. "KeyboardReference/JJKey.md".</summary>
        internal static string RelativePath(string fullPath) =>
            Path.GetRelativePath(MdRoot, fullPath).Replace('\\', '/');

        private static readonly Regex PageLine = new(
            @"^(?<planned>Planned:\s+)?(?<path>(?:[A-Za-z0-9]+/)?[A-Za-z0-9]+\.md)(?:#[^\s]+)?\s*$",
            RegexOptions.Compiled);

        /// <summary>
        /// The pages the outline lists as WRITTEN (page lines without the
        /// "Planned: " mark), as paths relative to md/.
        /// </summary>
        internal static IReadOnlyList<string> WrittenOutlinePages() => OutlinePageLines(planned: false);

        /// <summary>The pages the outline marks "Planned: ".</summary>
        internal static IReadOnlyList<string> PlannedOutlinePages() => OutlinePageLines(planned: true);

        private static IReadOnlyList<string> OutlinePageLines(bool planned)
        {
            var list = new List<string>();
            foreach (string line in File.ReadAllLines(OutlinePath))
            {
                var m = PageLine.Match(line.Trim());
                if (!m.Success) continue;
                if (m.Groups["planned"].Success == planned) list.Add(m.Groups["path"].Value);
            }
            return list;
        }
    }
}
