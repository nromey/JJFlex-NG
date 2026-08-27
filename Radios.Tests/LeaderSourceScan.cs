using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Reads the leader layer out of SOURCE: the chords the inventory
    /// advertises, and the chords the dispatcher's switch handles.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Extracted from <see cref="LeaderLayerConsistencyTests"/> in Sprint 36
    /// Track F, when the doc-coverage check (#265) needed the same inventory
    /// reader. Copying it would have been the exact defect both tests exist to
    /// catch — a second home for one fact — in the tests that police it.
    /// </para>
    /// <para>
    /// It reads source rather than loading types because Radios.Tests cannot
    /// load the WPF assembly, and because the thing being verified is literal
    /// text written by people. Same family as LexiconKeyCoverageTests.
    /// </para>
    /// </remarks>
    internal static class LeaderSourceScan
    {
        internal sealed record InventoryEntry(string Display, string Description, List<string> Excluded);

        internal static HashSet<Keys> RealAdvertised(out int entryCount)
        {
            var entries = InventoryEntries(ReadSource(Path.Combine("JJFlexWpf", "KeyInventory.cs")));
            entryCount = entries.Count;
            return Advertised(entries);
        }

        internal static List<InventoryEntry> RealInventoryEntries()
            => InventoryEntries(ReadSource(Path.Combine("JJFlexWpf", "KeyInventory.cs")));

        internal static HashSet<Keys> RealHandled()
            => SwitchCases(ReadSource(Path.Combine("JJFlexWpf", "KeyCommands.cs")), "DoLeaderCommand");

        internal static HashSet<Keys> Advertised(List<InventoryEntry> entries)
        {
            var set = new HashSet<Keys>();
            foreach (var e in entries)
            {
                var chords = LeaderChordParser.ParseDisplay(e.Display, e.Excluded);
                Assert.True(chords.Count > 0,
                    $"LeaderCommands entry '{e.Display}' parsed to no chords — either the entry "
                    + "or LeaderChordParser needs fixing; an unparseable entry would otherwise be "
                    + "silently exempt from this whole test");
                foreach (var c in chords) set.Add(c);
            }
            return set;
        }

        /// <summary>
        /// Every LeaderCommands entry's KeyDisplay plus its ExcludedKeys, read
        /// from the inventory source.
        /// </summary>
        internal static List<InventoryEntry> InventoryEntries(string source)
        {
            var result = new List<InventoryEntry>();

            int start = source.IndexOf("FixedKeyEntry[] LeaderCommands", StringComparison.Ordinal);
            if (start < 0) return result;
            int end = source.IndexOf("};", start, StringComparison.Ordinal);
            if (end < 0) return result;
            string region = source.Substring(start, end - start);

            // Entry chunks: each begins with the array's fixed first two
            // arguments. The chunk runs to the next entry (or region end), so
            // an ExcludedKeys initializer stays with ITS entry — applying the
            // exclusion globally would delete the separately-advertised
            // Shift+F row along with the range's gap.
            var starts = Regex.Matches(region, @"new\s*\(\s*""Leader""\s*,\s*""Leader key""\s*,")
                .Cast<Match>().ToList();
            for (int i = 0; i < starts.Count; i++)
            {
                int from = starts[i].Index;
                int to = i + 1 < starts.Count ? starts[i + 1].Index : region.Length;
                string chunk = region.Substring(from, to - from);

                // Display AND description in one match: entries wrap across
                // lines, so \s (which matches newlines in .NET) does the work.
                var display = Regex.Match(chunk,
                    @"new\s*\(\s*""Leader""\s*,\s*""Leader key""\s*,\s*""([^""]+)""\s*,\s*""([^""]+)""");
                if (!display.Success) continue;

                var excluded = new List<string>();
                var ex = Regex.Match(chunk, @"ExcludedKeys\s*=\s*new\[\]\s*\{([^}]*)\}");
                if (ex.Success)
                {
                    foreach (Match m in Regex.Matches(ex.Groups[1].Value, @"""([^""]+)"""))
                        excluded.Add(m.Groups[1].Value);
                }

                result.Add(new InventoryEntry(
                    display.Groups[1].Value, display.Groups[2].Value, excluded));
            }
            return result;
        }

        /// <summary>
        /// Every <c>case Keys....:</c> label in the named method's body, as
        /// parsed chords. Strings and comments are blanked first so neither
        /// can plant a phantom label, and the body is bounded by real brace
        /// counting rather than a landmark comment.
        /// </summary>
        internal static HashSet<Keys> SwitchCases(string source, string methodName)
        {
            var result = new HashSet<Keys>();

            int sig = source.IndexOf(methodName + "(Keys k)", StringComparison.Ordinal);
            if (sig < 0) return result;

            string clean = BlankStringsAndComments(source);
            int open = clean.IndexOf('{', sig);
            if (open < 0) return result;

            int depth = 0, i = open;
            for (; i < clean.Length; i++)
            {
                if (clean[i] == '{') depth++;
                else if (clean[i] == '}' && --depth == 0) break;
            }
            string body = clean.Substring(open, i - open);

            foreach (Match m in Regex.Matches(body, @"case\s+((?:Keys\.\w+\s*\|?\s*)+):"))
            {
                Keys chord = Keys.None;
                bool ok = true;
                foreach (Match token in Regex.Matches(m.Groups[1].Value, @"Keys\.(\w+)"))
                {
                    if (Enum.TryParse(token.Groups[1].Value, out Keys part)) chord |= part;
                    else ok = false;
                }
                if (ok && chord != Keys.None) result.Add(chord);
            }
            return result;
        }

        /// <summary>
        /// Replace string-literal and comment CONTENT with spaces, length
        /// preserved, so brace counting and label matching see only code.
        /// </summary>
        internal static string BlankStringsAndComments(string source)
        {
            var sb = new StringBuilder(source);
            int i = 0;
            while (i < sb.Length)
            {
                char c = sb[i];
                if (c == '"')
                {
                    bool verbatim = i > 0 && sb[i - 1] == '@';
                    i++;
                    while (i < sb.Length)
                    {
                        if (verbatim && sb[i] == '"' && i + 1 < sb.Length && sb[i + 1] == '"')
                        { sb[i] = ' '; sb[i + 1] = ' '; i += 2; continue; }
                        if (sb[i] == '"') { i++; break; }
                        if (!verbatim && sb[i] == '\\' && i + 1 < sb.Length)
                        { sb[i] = ' '; sb[i + 1] = ' '; i += 2; continue; }
                        sb[i] = ' ';
                        i++;
                    }
                    continue;
                }
                if (c == '/' && i + 1 < sb.Length && sb[i + 1] == '/')
                {
                    while (i < sb.Length && sb[i] != '\n') { sb[i] = ' '; i++; }
                    continue;
                }
                if (c == '/' && i + 1 < sb.Length && sb[i + 1] == '*')
                {
                    sb[i] = ' '; sb[i + 1] = ' '; i += 2;
                    while (i < sb.Length && !(sb[i] == '*' && i + 1 < sb.Length && sb[i + 1] == '/'))
                    { sb[i] = ' '; i++; }
                    if (i + 1 < sb.Length) { sb[i] = ' '; sb[i + 1] = ' '; i += 2; }
                    continue;
                }
                i++;
            }
            return sb.ToString();
        }

        internal static string ReadSource(string relative)
        {
            string path = Path.Combine(RepoRoot(), relative);
            Assert.True(File.Exists(path), "source not found: " + path);
            return File.ReadAllText(path);
        }

        internal static string RepoRoot()
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
