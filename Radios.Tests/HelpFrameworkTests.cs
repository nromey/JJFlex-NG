using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Radios.SmartLink;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The places in the code that name a help page, held to the help tree and
    /// its outline (#826). The help build checks the pages against each other;
    /// these check the code's side of the seam, which the build cannot see.
    /// </summary>
    public class HelpFrameworkTests
    {
        // ────────────────────────────────────────────────────────────────
        //  The shared lookup, proved before anything relies on it
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void The_lookup_finds_a_page_inside_its_folder()
        {
            string path = HelpPages.PathOf("JJKey.md");
            Assert.Equal("KeyboardReference/JJKey.md", HelpPages.RelativePath(path));
        }

        [Fact]
        public void The_lookup_refuses_a_name_no_page_has()
        {
            Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => HelpPages.PathOf("NoSuchHelpPage.md"));
        }

        [Fact]
        public void The_lookup_refuses_a_name_in_the_wrong_case()
        {
            Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => HelpPages.PathOf("jjkey.md"));
        }

        [Fact]
        public void The_outline_reader_sees_written_and_planned_pages()
        {
            // Positive control for every check below that consults the outline:
            // a reader that saw nothing would make them pass vacuously.
            var written = HelpPages.WrittenOutlinePages();
            var planned = HelpPages.PlannedOutlinePages();
            Assert.True(written.Count >= 60, "only " + written.Count + " written pages read from outline.md");
            Assert.Contains("KeyboardReference/JJKey.md", written);
            Assert.Contains("AudioWorkshop/ListenBack.md", planned);
            Assert.DoesNotContain("AudioWorkshop/ListenBack.md", written);
        }

        // ────────────────────────────────────────────────────────────────
        //  F1: every page HelpLauncher opens exists and is in the outline
        // ────────────────────────────────────────────────────────────────

        private static readonly Regex MapEntry =
            new(@"\{\s*""(?<key>\w+)""\s*,\s*""pages/(?<page>[^""]+)\.htm""\s*\}", RegexOptions.Compiled);

        private static Dictionary<string, string> ContextMap()
        {
            string src = File.ReadAllText(Path.Combine(LeaderSourceScan.RepoRoot(), "JJFlexWpf", "HelpLauncher.cs"));
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in MapEntry.Matches(src)) map[m.Groups["key"].Value] = m.Groups["page"].Value + ".md";
            return map;
        }

        [Fact]
        public void Every_page_HelpLauncher_opens_is_a_written_page_in_the_outline()
        {
            var map = ContextMap();
            Assert.True(map.Count >= 20,
                "only " + map.Count + " ContextMap entries were read from HelpLauncher.cs; the reader has "
                + "gone blind and every check below would pass on nothing");

            var written = new HashSet<string>(HelpPages.WrittenOutlinePages(), StringComparer.Ordinal);
            var problems = new List<string>();
            foreach (var (key, page) in map)
            {
                string onDisk = Path.Combine(HelpPages.MdRoot, page.Replace('/', Path.DirectorySeparatorChar));
                if (page != "WhatsNew.md" && !File.Exists(onDisk))
                    problems.Add(key + " opens pages/" + page + ", but docs/help/md/" + page + " does not exist");
                else if (!written.Contains(page))
                    problems.Add(key + " opens pages/" + page + ", which outline.md does not list as a written page");
            }
            Assert.True(problems.Count == 0,
                "F1 would open a page the help does not build: " + string.Join("; ", problems)
                + ". The compiled help then opens its front page instead, with no error anywhere.");
        }

        [Fact]
        public void Every_help_key_the_network_tab_asks_for_is_in_HelpLauncher()
        {
            var map = ContextMap();
            foreach (string key in new[]
            {
                SessionStatusMessages.HelpKeyNetworkDiagnostics,
                SessionStatusMessages.HelpKeyManualPortForwarding,
                SessionStatusMessages.HelpKeyUpnp,
            })
            {
                Assert.True(map.ContainsKey(key),
                    "SessionStatusMessages.HelpDocFor can return \"" + key + "\", which HelpLauncher.ContextMap "
                    + "does not map, so the Network tab's Help button would open the front page.");
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  The Know Your Radio pages the app embeds
        // ────────────────────────────────────────────────────────────────

        private static readonly Regex EmbeddedPage = new(
            @"<EmbeddedResource\s+Include=""\.\.\\docs\\help\\md\\(?<path>[^""]+\.md)""\s+LogicalName=""JJFlexWpf\.Help\.(?<name>[^""]+)""",
            RegexOptions.Compiled);

        private static List<(string Path, string LogicalName)> EmbeddedPages()
        {
            string proj = File.ReadAllText(Path.Combine(LeaderSourceScan.RepoRoot(), "JJFlexWpf", "JJFlexWpf.csproj"));
            return EmbeddedPage.Matches(proj)
                .Select(m => (m.Groups["path"].Value.Replace('\\', '/'), m.Groups["name"].Value))
                .ToList();
        }

        [Fact]
        public void Every_embedded_panel_guide_exists_and_RadioPanelGuide_asks_for_it_by_its_resource_name()
        {
            var pages = EmbeddedPages();
            Assert.True(pages.Count >= 5, "only " + pages.Count + " embedded help pages read from JJFlexWpf.csproj");

            foreach (var (path, logical) in pages)
            {
                Assert.True(File.Exists(Path.Combine(HelpPages.MdRoot, path)),
                    "JJFlexWpf.csproj embeds docs/help/md/" + path + ", which does not exist");
                Assert.Equal(Path.GetFileName(path), logical);
            }

            // A resource name RadioPanelGuide asks for that nothing embeds is a
            // guide that silently returns null, and its button silently hides.
            string guide = File.ReadAllText(Path.Combine(LeaderSourceScan.RepoRoot(), "JJFlexWpf", "RadioPanelGuide.cs"));
            var asked = Regex.Matches(guide, @"\(""\d{4}"",\s*""(?<res>[^""]+\.md)""")
                .Select(m => m.Groups["res"].Value).Distinct().ToList();
            Assert.True(asked.Count >= 5, "only " + asked.Count + " resources read from RadioPanelGuide.Guides");
            var logicalNames = pages.Select(p => p.LogicalName).ToHashSet(StringComparer.Ordinal);
            foreach (string res in asked)
                Assert.True(logicalNames.Contains(res),
                    "RadioPanelGuide asks for the resource JJFlexWpf.Help." + res + ", which JJFlexWpf.csproj does not embed");
        }

        private static readonly Regex AnyLink = new(@"\]\((?<target>[^)\s]+)\)", RegexOptions.Compiled);

        [Fact]
        public void No_embedded_panel_guide_links_another_page()
        {
            // The in-app panel guide renders these pages itself, and a link
            // there to another help page (or to a #section) goes nowhere.
            foreach (var (path, _) in EmbeddedPages())
            {
                string text = File.ReadAllText(Path.Combine(HelpPages.MdRoot, path));
                var local = AnyLink.Matches(text)
                    .Select(m => m.Groups["target"].Value)
                    .Where(t => !t.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                             && !t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                             && !t.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                Assert.True(local.Count == 0,
                    path + " is embedded in the app's panel guide and links " + string.Join(", ", local)
                    + ", which goes nowhere inside that dialog.");
            }
        }

        [Fact]
        public void The_link_reader_sees_a_link_when_there_is_one()
        {
            // Positive control for the check above.
            Assert.Single(AnyLink.Matches("see [the 6300](Flex6300.md) for more"));
        }

        // ────────────────────────────────────────────────────────────────
        //  The keyword block every page opens with
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Every_page_opens_with_a_keyword_block()
        {
            var pages = HelpPages.AllPageFiles()
                .Where(f => HelpPages.RelativePath(f) != "WhatsNew.md") // the changelog, imported at build time
                .ToList();
            Assert.True(pages.Count >= 60, "only " + pages.Count + " pages found under " + HelpPages.MdRoot);

            var missing = new List<string>();
            foreach (string f in pages)
            {
                string text = File.ReadAllText(f).TrimStart('﻿');
                string stripped = HelpFrontMatter.Strip(text);
                bool hasBlock = text.StartsWith("---") && stripped.Length < text.Length;
                string block = hasBlock ? text.Substring(0, text.Length - stripped.Length) : "";
                if (!hasBlock || !Regex.IsMatch(block, @"(?m)^keywords:"))
                    missing.Add(HelpPages.RelativePath(f));
            }
            Assert.True(missing.Count == 0,
                "These pages do not open with a keyword block (---, keywords:, ---), which the help's Index "
                + "is built from: " + string.Join(", ", missing)
                + ". A page with no keywords still carries the block, as  keywords: []");
        }

        [Fact]
        public void A_real_page_shows_from_its_heading_once_the_block_is_removed()
        {
            string text = HelpPages.Read("Flex6300.md");
            Assert.StartsWith("---", text.TrimStart('﻿'));
            string shown = HelpFrontMatter.Strip(text);
            Assert.StartsWith("# ", shown);
            Assert.DoesNotContain("keywords:", shown);
        }

        [Theory]
        [InlineData("---\nkeywords:\n  - a\n---\n\n# Title\n\nBody\n", "# Title\n\nBody\n")]
        [InlineData("---\r\nkeywords: []\r\n---\r\n\r\n# Title\r\n", "# Title\r\n")]
        [InlineData("﻿---\nkeywords: []\n---\n# Title\n", "# Title\n")]
        [InlineData("---\nkeywords: []\n...\n# Title\n", "# Title\n")]
        public void Strip_removes_the_block_and_the_blank_lines_after_it(string page, string expected)
        {
            Assert.Equal(expected, HelpFrontMatter.Strip(page));
        }

        [Theory]
        [InlineData("# Title\n\nText\n\n---\n\nMore\n")]          // a later rule is a rule
        [InlineData("---\nkeywords: []\n# Never closed\n")]      // no closing line: not a block
        [InlineData("")]
        public void Strip_leaves_anything_else_alone(string page)
        {
            Assert.Equal(page, HelpFrontMatter.Strip(page));
        }

        // ────────────────────────────────────────────────────────────────
        //  Tables, which this project's help never uses (#532)
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The pages that still carry a markdown table, each waiting on #532's
        /// rewrite. The list can only shrink: an excused page with no table left
        /// fails the companion test below until it is taken off.
        /// </summary>
        private static readonly string[] PagesWithATableToday =
        {
            "JJFlexibleHome/BandNavigation.md",
            "JJFlexibleHome/ModeSwitching.md",
            "Logging/Logging.md",
            "Receiver/FiltersAndDsp.md",
            "Sounds/MeterTones.md",
        };

        private static bool HasTable(string file) =>
            File.ReadAllLines(file).Any(l => l.TrimStart().StartsWith("|"));

        [Fact]
        public void No_help_page_has_a_markdown_table_except_the_ones_waiting_on_532()
        {
            var offenders = HelpPages.AllPageFiles()
                .Where(f => HelpPages.RelativePath(f) != "WhatsNew.md")
                .Where(HasTable)
                .Select(HelpPages.RelativePath)
                .Where(r => !PagesWithATableToday.Contains(r))
                .ToList();
            Assert.True(offenders.Count == 0,
                "These help pages have a markdown table: " + string.Join(", ", offenders)
                + ". The help is prose and bullets, never tables; a screen reader cannot move "
                + "through a table comfortably, and the help is read by someone who is stuck.");
        }

        [Fact]
        public void Every_page_excused_from_the_table_ban_still_has_a_table()
        {
            foreach (string rel in PagesWithATableToday)
            {
                string f = Path.Combine(HelpPages.MdRoot, rel);
                Assert.True(File.Exists(f), rel + " is excused from the table ban but does not exist; take it off the list");
                Assert.True(HasTable(f), rel + " no longer has a table: take it off PagesWithATableToday");
            }
        }
    }
}
