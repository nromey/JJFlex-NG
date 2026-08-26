using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Compares what the leader layer ADVERTISES against what its switch
    /// actually HANDLES, both directions (#183).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> The Ctrl+J help said "H or ?, List the leader
    /// key commands" while "?" fell through to the unknown-command arm, and
    /// both statements lived happily in the same file — the help is a table
    /// (<c>KeyInventory.LeaderCommands</c>), the behaviour is a switch
    /// (<c>KeyCommands.DoLeaderCommand</c>), and NOTHING compared the two.
    /// Noel found it by pressing the key on 2026-08-22. This test would have
    /// caught it the day it was written: "?" arrives as Oem2|Shift, the
    /// switch carried only bare Oem2, and the advertised-but-unhandled sweep
    /// below reports exactly that shape.
    /// </para>
    /// <para>
    /// <b>Both directions matter.</b> Advertised-but-unhandled is a promise
    /// the app breaks when pressed. Handled-but-unadvertised is BlindCat
    /// anti-pattern #1 — a hotkey nobody can discover — and the inventory
    /// row it is missing from also feeds the Keys dialog and the Command
    /// Finder, so the chord would work perfectly and be findable nowhere.
    /// </para>
    /// <para>
    /// It reads SOURCE, in the LexiconKeyCoverageTests family, because
    /// Radios.Tests cannot load the WPF assembly and because the thing being
    /// verified is literal text written by people. The advertised strings run
    /// through the PRODUCTION parser (<see cref="LeaderChordParser"/>) — the
    /// same code the #206 near-miss lookup uses at runtime — so the test also
    /// proves that parser against every real inventory row on every run.
    /// </para>
    /// </remarks>
    public class LeaderLayerConsistencyTests
    {
        // ────────────────────────────────────────────────────────────────
        //  Prove the instruments before trusting their silence
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void The_switch_scanner_reads_case_labels_with_and_without_modifiers()
        {
            const string sample = @"
                private bool DoLeaderCommand(Keys k)
                {
                    switch (k)
                    {
                        case Keys.N:
                            DoThing();
                            break;
                        case Keys.A | Keys.Shift: Jump(0); break;
                        case Keys.Oem2 | Keys.Shift:
                        case Keys.H:
                            Help();
                            break;
                        default:
                            break;
                    }
                    return true;
                }";

            var handled = SwitchCases(sample, "DoLeaderCommand");

            Assert.Equal(4, handled.Count);
            Assert.Contains(Keys.N, handled);
            Assert.Contains(Keys.A | Keys.Shift, handled);
            Assert.Contains(Keys.Oem2 | Keys.Shift, handled);
            Assert.Contains(Keys.H, handled);
        }

        [Fact]
        public void The_switch_scanner_is_not_fooled_by_strings_or_comments()
        {
            const string sample = @"
                private bool DoLeaderCommand(Keys k)
                {
                    // a comment mentioning case Keys.Z: which is not a label
                    var s = ""case Keys.Q:"";
                    switch (k)
                    {
                        case Keys.M:
                            break;
                    }
                    return true;
                }";

            var handled = SwitchCases(sample, "DoLeaderCommand");

            Assert.Equal(new[] { Keys.M }, handled.OrderBy(k => k).ToArray());
        }

        [Fact]
        public void The_inventory_scanner_reads_displays_and_their_exclusions()
        {
            const string sample = @"
    private static readonly FixedKeyEntry[] LeaderCommands =
    {
        new(""Leader"", ""Leader key"", ""Ctrl+J, N"", ""Toggle a thing"",
            new[] { ""thing"" }, ""Radio"", ""DSP""),
        new(""Leader"", ""Leader key"", ""Ctrl+J, Shift+A through Shift+C"", ""Jump"",
            new[] { ""jump"" }, ""Radio"", ""General"")
            { ExcludedKeys = new[] { ""Ctrl+J, Shift+B"" } },
    };";

            var entries = InventoryEntries(sample);

            Assert.Equal(2, entries.Count);
            Assert.Equal("Ctrl+J, N", entries[0].Display);
            Assert.Empty(entries[0].Excluded);
            Assert.Equal(new[] { "Ctrl+J, Shift+B" }, entries[1].Excluded);

            var chords = Advertised(entries);
            Assert.Contains(Keys.N, chords);
            Assert.Contains(Keys.A | Keys.Shift, chords);
            Assert.Contains(Keys.C | Keys.Shift, chords);
            Assert.DoesNotContain(Keys.B | Keys.Shift, chords);
        }

        [Fact]
        public void A_planted_advertised_but_unhandled_key_is_reported()
        {
            // The register's demanded positive control: plant the exact shape
            // of the original bug and confirm the comparison fails on it. A
            // consistency check whose silence has never been tested is
            // decorative.
            var advertised = new HashSet<Keys> { Keys.N, Keys.Oem2 | Keys.Shift };
            var handled = new HashSet<Keys> { Keys.N, Keys.Oem2 };   // bare only — the real bug

            var missing = advertised.Except(handled).ToList();

            Assert.Single(missing);
            Assert.Equal(Keys.Oem2 | Keys.Shift, missing[0]);
        }

        [Fact]
        public void A_planted_handled_but_unadvertised_key_is_reported()
        {
            var advertised = new HashSet<Keys> { Keys.N };
            var handled = new HashSet<Keys> { Keys.N, Keys.Z | Keys.Control };

            var undocumented = handled.Except(advertised).ToList();

            Assert.Single(undocumented);
            Assert.Equal(Keys.Z | Keys.Control, undocumented[0]);
        }

        // ────────────────────────────────────────────────────────────────
        //  The sweep — real inventory against the real switch
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Every_advertised_leader_chord_is_handled_by_the_switch()
        {
            var advertised = RealAdvertised(out _);
            var handled = RealHandled();

            // Escape is advertised ("Ctrl+J, Escape — Cancel leader mode")
            // but handled BEFORE the switch, in DoCommand's leader-active
            // block. Its existence is asserted separately below.
            advertised.Remove(Keys.Escape);

            var broken = advertised.Except(handled).OrderBy(k => k).ToList();
            Assert.True(broken.Count == 0,
                "Advertised in KeyInventory.LeaderCommands but NOT handled by DoLeaderCommand — "
                + "the help and Keys dialog promise these and the switch breaks the promise: "
                + string.Join(", ", broken));
        }

        [Fact]
        public void Every_chord_the_switch_handles_is_advertised()
        {
            var advertised = RealAdvertised(out _);
            var handled = RealHandled();

            var undocumented = handled.Except(advertised).OrderBy(k => k).ToList();
            Assert.True(undocumented.Count == 0,
                "Handled by DoLeaderCommand but absent from KeyInventory.LeaderCommands — "
                + "these work and are discoverable NOWHERE (no Ctrl+J help, no Keys dialog, "
                + "no Command Finder), which is BlindCat anti-pattern #1: "
                + string.Join(", ", undocumented));
        }

        [Fact]
        public void The_leader_escape_cancel_exists_outside_the_switch()
        {
            // The one advertised chord the switch never sees: DoCommand's
            // leader-active block consumes Escape as the cancel before
            // dispatch. If that block ever disappears, the advertised
            // "Ctrl+J, Escape" becomes a lie this catches.
            string src = ReadSource(Path.Combine("JJFlexWpf", "KeyCommands.cs"));
            Assert.Contains("EarconPlayer.LeaderCancelTone()", src);
            Assert.Contains("settings.leader.cancelled", src);
        }

        [Fact]
        public void Every_oem_key_case_also_handles_its_shifted_form()
        {
            // The root of the original bug, generalised: this switch carries
            // modifier bits, and punctuation reached through Shift arrives
            // WITH Shift. A bare Oem case for a key whose glyph needs Shift is
            // a binding that compiles, reads correctly, and never fires. Any
            // deliberately shift-free Oem key goes in the allowlist with its
            // reason — currently there are none.
            var shiftFreeByDesign = new HashSet<Keys>();

            var handled = RealHandled();
            var violations = new List<string>();

            foreach (var chord in handled)
            {
                Keys code = chord & Keys.KeyCode;
                if (!code.ToString().StartsWith("Oem", StringComparison.Ordinal)) continue;
                if (shiftFreeByDesign.Contains(code)) continue;

                if (!handled.Contains(code) || !handled.Contains(code | Keys.Shift))
                    violations.Add(code + " (have " + chord + ", need both bare and Shift forms)");
            }

            Assert.True(violations.Count == 0,
                "Oem keys in the leader switch must handle both bare and shifted arrival forms, "
                + "or be listed shift-free by design: " + string.Join("; ", violations.Distinct()));
        }

        [Fact]
        public void The_sweep_actually_saw_the_layer()
        {
            // Positive control on the sweep itself: a broken region-finder
            // returning empty sets would make both direction checks pass
            // vacuously — the exact silent-success failure this project keeps
            // finding. The layer holds ~28 single chords, a 7-chord slice
            // range, help and Escape; the switch ~38 cases.
            var advertised = RealAdvertised(out int entryCount);
            var handled = RealHandled();

            Assert.True(entryCount >= 25,
                $"only {entryCount} LeaderCommands entries parsed — the inventory region was not found");
            Assert.True(advertised.Count >= 30,
                $"only {advertised.Count} advertised chords — the parser or region is broken");
            Assert.True(handled.Count >= 30,
                $"only {handled.Count} handled chords — the switch scanner is broken");
        }

        // ────────────────────────────────────────────────────────────────
        //  Real-source extraction
        // ────────────────────────────────────────────────────────────────

        private sealed record InventoryEntry(string Display, List<string> Excluded);

        private static HashSet<Keys> RealAdvertised(out int entryCount)
        {
            string src = ReadSource(Path.Combine("JJFlexWpf", "KeyInventory.cs"));
            var entries = InventoryEntries(src);
            entryCount = entries.Count;
            return Advertised(entries);
        }

        private static HashSet<Keys> RealHandled()
        {
            string src = ReadSource(Path.Combine("JJFlexWpf", "KeyCommands.cs"));
            return SwitchCases(src, "DoLeaderCommand");
        }

        private static HashSet<Keys> Advertised(List<InventoryEntry> entries)
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
        private static List<InventoryEntry> InventoryEntries(string source)
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

                var display = Regex.Match(chunk,
                    @"new\s*\(\s*""Leader""\s*,\s*""Leader key""\s*,\s*""([^""]+)""");
                if (!display.Success) continue;

                var excluded = new List<string>();
                var ex = Regex.Match(chunk, @"ExcludedKeys\s*=\s*new\[\]\s*\{([^}]*)\}");
                if (ex.Success)
                {
                    foreach (Match m in Regex.Matches(ex.Groups[1].Value, @"""([^""]+)"""))
                        excluded.Add(m.Groups[1].Value);
                }

                result.Add(new InventoryEntry(display.Groups[1].Value, excluded));
            }
            return result;
        }

        /// <summary>
        /// Every <c>case Keys....:</c> label in the named method's body, as
        /// parsed chords. Strings and comments are blanked first so neither
        /// can plant a phantom label, and the body is bounded by real brace
        /// counting rather than a landmark comment.
        /// </summary>
        private static HashSet<Keys> SwitchCases(string source, string methodName)
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
        private static string BlankStringsAndComments(string source)
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

        private static string ReadSource(string relative)
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
                if (File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            return AppContext.BaseDirectory;
        }
    }
}
