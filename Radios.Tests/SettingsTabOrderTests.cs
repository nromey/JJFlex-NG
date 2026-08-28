using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The Settings dialog's tab order: category, then the settings it
    /// selected, then OK / Apply / Cancel last (#301).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Found 2026-08-27 by Noel at the keyboard: "settings tab, category seems
    /// to be right next to ok cancel in the tab order, ok and cancel should be
    /// last, category first, so you tab to get to the actual settings." The
    /// category list is the NAVIGATION control, so Tab from it should carry you
    /// INTO what you just chose. It carried you out of the dialog instead.
    /// </para>
    /// <para>
    /// <b>The XAML already said the right thing, which is what makes this worth
    /// a test.</b> It declared TabIndex 1 on the category list, 2 on the tab
    /// host and 3 on the button panel, with a comment explaining that the order
    /// was set explicitly. Two of those three numbers were read by nobody.
    /// </para>
    /// <para>
    /// KeyboardNavigation.TabIndex does not inherit, and WPF consults it only
    /// on elements that are a TAB STOP or a navigation GROUP. Verified against
    /// this machine's .NET 10 property metadata on 2026-08-27: every Panel has
    /// Focusable=False, so a StackPanel is not a tab stop; TabNavigation
    /// defaults to Continue on both StackPanel and TabControl, so neither is a
    /// group; and the CategoryTabControl style sets IsTabStop=False and
    /// Focusable=False on the tab host besides. Two inert indexes meant the
    /// three buttons and every control in every category all sat at the default
    /// Int32.MaxValue, where ties fall back to tree order — and the button panel
    /// is declared FIRST so it docks across the full width. Hence buttons before
    /// settings, exactly as reported.
    /// </para>
    /// <para>
    /// TabNavigation="Local" is the fix: it makes each container a group, which
    /// is what gives its TabIndex a reader, and unlike Cycle or Contained it
    /// lets Tab walk out the far end instead of caging the operator inside.
    /// </para>
    /// <para>
    /// Source-read, in the ReadOnlyNotesNotTabStopsTests family, because
    /// Radios.Tests cannot load the WPF assembly and because what is being
    /// verified is literal markup written by people. <b>This cannot prove where
    /// focus LANDS</b> — only pressing Tab on a real build can, and the report
    /// says so. What it can prove is that no index in this file is inert again.
    /// </para>
    /// </remarks>
    public class SettingsTabOrderTests
    {
        private const string SettingsXaml = "JJFlexWpf/Dialogs/SettingsDialog.xaml";

        /// <summary>One place in the markup that sets a tab index.</summary>
        private sealed record TabIndexSite(string Element, int Index, bool DeclaresTabNavigation);

        // ────────────────────────────────────────────────────────────────
        //  Prove the instrument before trusting it
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void The_scanner_finds_the_three_ordered_containers()
        {
            // The positive control for everything below. A parser that found
            // nothing would pass every assertion in this file silently, which
            // is the failure mode a source-reading test is most prone to.
            var sites = Scan();

            Assert.Equal(3, sites.Count);
            Assert.Contains(sites, s => s.Element == "ListBox");
            Assert.Contains(sites, s => s.Element == "local:CategoryTabHost");
            Assert.Contains(sites, s => s.Element == "StackPanel");
        }

        [Fact]
        public void The_scanner_ignores_tab_indexes_written_inside_comments()
        {
            // The file's own comments now discuss TabIndex at length. If the
            // scanner counted those it would report sites that do not exist,
            // and the count assertion above would be measuring prose.
            const string sample = """
                <!-- TabIndex="9" appears here and must not be seen -->
                <StackPanel KeyboardNavigation.TabNavigation="Local"
                            KeyboardNavigation.TabIndex="3"/>
                """;

            var sites = Parse(sample);

            Assert.Single(sites);
            Assert.Equal(3, sites[0].Index);
        }

        // ────────────────────────────────────────────────────────────────
        //  The order itself
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void The_commit_buttons_come_after_the_settings_they_commit()
        {
            var sites = Scan();

            int category = sites.Single(s => s.Element == "ListBox").Index;
            int content = sites.Single(s => s.Element == "local:CategoryTabHost").Index;
            int buttons = sites.Single(s => s.Element == "StackPanel").Index;

            // Category, then the content it selected, then the way out.
            // Commit buttons are terminal by nature; nothing should sit behind
            // them, and the operator should not have to pass them to reach what
            // they just chose.
            Assert.True(category < content,
                $"category list ({category}) must come before the settings ({content})");
            Assert.True(content < buttons,
                $"the settings ({content}) must come before OK/Apply/Cancel ({buttons})");
        }

        // ────────────────────────────────────────────────────────────────
        //  The bug itself: an index nothing reads
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Every_tab_index_in_this_file_is_on_something_that_can_carry_one()
        {
            // A TabIndex is consulted only on a tab stop or a navigation group.
            // The ListBox is both a Control and — TabNavigation defaults to
            // Once on ListBox — a group already, so it needs nothing declared.
            // The other two are a Panel and a control the CategoryTabControl
            // style makes non-focusable, so each must say TabNavigation for its
            // number to mean anything at all.
            var mustDeclare = new[] { "local:CategoryTabHost", "StackPanel" };

            foreach (string element in mustDeclare)
            {
                var site = Scan().Single(s => s.Element == element);
                Assert.True(site.DeclaresTabNavigation,
                    $"<{element}> sets TabIndex={site.Index} but declares no "
                    + "KeyboardNavigation.TabNavigation, so WPF never reads that index "
                    + "and the element falls to Int32.MaxValue with everything else (#301)");
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  Scanning
        // ────────────────────────────────────────────────────────────────

        private static List<TabIndexSite> Scan() => Parse(Source(SettingsXaml));

        /// <summary>
        /// Every element in the markup that sets a tab index, with the element
        /// name and whether the same element also declares TabNavigation.
        /// </summary>
        /// <remarks>
        /// Hand-scanned rather than regexed. The attribute values in this file
        /// are long prose sentences, so a pattern that has to guess where a tag
        /// ends gets it wrong; walking the text and respecting quotes does not.
        /// </remarks>
        private static List<TabIndexSite> Parse(string xaml)
        {
            string text = StripComments(xaml);
            var sites = new List<TabIndexSite>();

            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '<') continue;
                if (i + 1 < text.Length && (text[i + 1] == '/' || text[i + 1] == '!' || text[i + 1] == '?'))
                    continue;

                int end = EndOfTag(text, i);
                string tag = text[i..end];

                int nameEnd = i + 1;
                while (nameEnd < end && !char.IsWhiteSpace(text[nameEnd])
                       && text[nameEnd] != '>' && text[nameEnd] != '/')
                    nameEnd++;
                string name = text[(i + 1)..nameEnd];

                int idx = ReadIntAttribute(tag, "TabIndex");
                if (idx >= 0)
                    sites.Add(new TabIndexSite(name, idx,
                        tag.Contains("KeyboardNavigation.TabNavigation=", StringComparison.Ordinal)));

                i = end - 1;
            }

            return sites;
        }

        /// <summary>Index just past this tag's closing '&gt;', quotes respected.</summary>
        private static int EndOfTag(string text, int start)
        {
            bool inQuote = false;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"') inQuote = !inQuote;
                else if (c == '>' && !inQuote) return i + 1;
            }
            return text.Length;
        }

        /// <summary>
        /// The value of TabIndex on this tag, or -1. Matches both the bare
        /// <c>TabIndex</c> (Control's own property) and the attached
        /// <c>KeyboardNavigation.TabIndex</c> — they are the same property, and
        /// this file uses both spellings.
        /// </summary>
        private static int ReadIntAttribute(string tag, string attribute)
        {
            int at = tag.IndexOf(attribute + "=\"", StringComparison.Ordinal);
            if (at < 0) return -1;
            int open = tag.IndexOf('"', at) + 1;
            int close = tag.IndexOf('"', open);
            if (close < 0) return -1;
            return int.TryParse(tag[open..close], out int value) ? value : -1;
        }

        private static string StripComments(string xaml)
        {
            var sb = new System.Text.StringBuilder(xaml.Length);
            int i = 0;
            while (i < xaml.Length)
            {
                int open = xaml.IndexOf("<!--", i, StringComparison.Ordinal);
                if (open < 0) { sb.Append(xaml, i, xaml.Length - i); break; }
                sb.Append(xaml, i, open - i);
                int close = xaml.IndexOf("-->", open, StringComparison.Ordinal);
                if (close < 0) break;
                i = close + 3;
            }
            return sb.ToString();
        }

        private static string Source(string relative)
        {
            string path = Path.Combine(RepoRoot(), relative);
            Assert.True(File.Exists(path), "source not found: " + path);
            return File.ReadAllText(path);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            return AppContext.BaseDirectory;
        }
    }
}
