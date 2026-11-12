using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #1068, ruled by Noel 2026-11-11 19:47 (J14): Settings' categories show
    /// the name NVDA says, and every route a sentence gives names a category by
    /// that one name. Radios became "Per-radio settings", Notifications
    /// "Verbosity and notifications settings", Updates "Update channel and
    /// auto-check preferences", Accessibility "Accessibility timing and input
    /// preferences"; PTT's spoken name became plain "PTT" to match its word.
    /// </summary>
    public sealed class SettingsRouteNamesTests
    {
        private static string Root => FieldKeyMapScan.RepoRoot();

        private static readonly Dictionary<string, string> Ruled = new(StringComparer.Ordinal)
        {
            ["PTT"] = "PTT",
            ["Per-radio settings"] = "Per-radio settings",
            ["Verbosity and notifications settings"] = "Verbosity and notifications settings",
            ["Update channel and auto-check preferences"] = "Update channel and auto-check preferences",
            ["Accessibility timing and input preferences"] = "Accessibility timing and input preferences",
        };

        [Fact]
        public void Every_settings_category_says_the_word_it_shows()
        {
            string xaml = File.ReadAllText(Path.Combine(Root, "JJFlexWpf", "Dialogs", "SettingsDialog.xaml"));
            var tabs = Regex.Matches(xaml, "<TabItem\\s+Header=\"([^\"]+)\"(?:\\s+AutomationProperties\\.Name=\"([^\"]+)\")?")
                .Select(m => (Header: m.Groups[1].Value, Name: m.Groups[2].Success ? m.Groups[2].Value : null))
                .ToList();
            Assert.True(tabs.Count >= 12, "only " + tabs.Count + " categories were read");

            // The ruled five, exactly.
            foreach (var (header, name) in Ruled)
                Assert.Contains(tabs, t => t.Header == header && t.Name == name);

            // And every category: its spoken name begins with what it shows
            // (case aside: "Radio Setup" is announced "Radio setup checklist").
            var off = tabs.Where(t => t.Name != null && !t.Name.StartsWith(t.Header, StringComparison.OrdinalIgnoreCase))
                .Select(t => "\"" + t.Header + "\" is announced \"" + t.Name + "\"").ToList();
            Assert.True(off.Count == 0, string.Join("; ", off));

            // The old words are gone as headers.
            foreach (string old in new[] { "Radios", "Notifications", "Updates", "Accessibility" })
                Assert.DoesNotContain(tabs, t => t.Header == old);
        }

        /// <summary>A category named in a route by a word that is not its name.</summary>
        internal static readonly Regex OldRoute = new(
            @"Settings, (?:on the |under )?(?:Radios|Notifications|Updates|Accessibility)\b(?! timing)"
            + @"|(?:on|in) the (?:Radios|Notifications|Updates|Accessibility) tab"
            + @"|\bRadios tab\b"
            + @"|under Radios\b"
            + @"|under Accessibility(?! timing)\b"
            + @"|Push to talk settings",
            RegexOptions.Compiled);

        [Theory]
        [InlineData("The setting is in Settings, under Radios.")]
        [InlineData("Visit Settings, Updates to install it.")]
        [InlineData("You can change this any time in Settings, on the Radios tab.")]
        [InlineData("switched off in Settings, Accessibility.")]
        [InlineData("listed under Settings, Notifications, Messages You Have Silenced")]
        [InlineData("is checked in Settings, under Accessibility;")]
        public void The_scan_sees_each_old_route(string seeded) => Assert.Matches(OldRoute, seeded);

        [Theory]
        [InlineData("The setting is in Settings, under Per-radio settings.")]
        [InlineData("switched off in Settings, Accessibility timing and input preferences.")]
        [InlineData("listed under Settings, Verbosity and notifications settings, Messages You Have Silenced")]
        public void The_scan_passes_each_ruled_route(string seeded) => Assert.DoesNotMatch(OldRoute, seeded);

        [Fact]
        public void No_lexicon_sentence_names_a_category_by_an_old_word()
        {
            var found = new List<string>();
            foreach (string file in Directory.GetFiles(Path.Combine(Root, "Radios", "Lexicon"), "*.json"))
            {
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                    if (!lines[i].TrimStart().StartsWith("\"_note", StringComparison.Ordinal) && OldRoute.IsMatch(lines[i]))
                        found.Add(Path.GetFileName(file) + ":" + (i + 1) + "  " + lines[i].Trim());
            }
            Assert.True(found.Count == 0, found.Count + " lexicon line(s) name a Settings category by an old word:\n" + string.Join("\n", found));
        }

        [Fact]
        public void No_shipped_code_string_names_a_category_by_an_old_word()
        {
            var found = new List<string>();
            int files = 0;
            foreach (string dir in new[] { "JJFlexWpf", "Radios" })
            {
                foreach (string file in Directory.GetFiles(Path.Combine(Root, dir), "*.*", SearchOption.AllDirectories)
                    .Where(f => (f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                             && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                             && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
                {
                    files++;
                    string text = File.ReadAllText(file);
                    if (file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                        text = Regex.Replace(text, "<!--.*?-->", m => new string('\n', m.Value.Count(c => c == '\n')), RegexOptions.Singleline);
                    string[] lines = text.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i].TrimStart();
                        if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("*", StringComparison.Ordinal)) continue;
                        // Only what is inside a string literal or an attribute: words in code are not shown.
                        foreach (Match lit in Regex.Matches(lines[i], "\"[^\"]*\""))
                            if (OldRoute.IsMatch(lit.Value))
                            {
                                found.Add(Path.GetRelativePath(Root, file) + ":" + (i + 1) + "  " + lines[i].Trim());
                                break;
                            }
                    }
                }
            }
            Assert.True(files > 300, "Only " + files + " files were read.");
            Assert.True(found.Count == 0, found.Count + " shipped string(s) name a Settings category by an old word:\n" + string.Join("\n", found));
        }
    }
}
