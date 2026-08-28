using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The Ctrl+F1 answer for the Frequency field (#184), proven as pure
    /// lookup and assembly — no window is ever constructed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The key rows come from KeyInventory SOURCE on every run</b>, in the
    /// LeaderLayerConsistencyTests family, because Radios.Tests cannot load
    /// the WPF assembly and because the thing being verified is literal text
    /// people wrote. The composed strings asserted here are therefore the
    /// strings the shipping build assembles from the same table — if an
    /// inventory row changes, these tests fail and the change is made
    /// consciously, in one place, with the spoken result read back.
    /// </para>
    /// <para>
    /// <b>The exact-string assertions are the review surface.</b> Everything
    /// in them is read aloud to an operator who is already lost; a failing
    /// diff here is a wording change Noel has not heard yet.
    /// </para>
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class TuningContextHelpTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(TuningContextHelpTests));

        public TuningContextHelpTests()
        {
            Lexicon.Load(Lexicon.Partitions);
        }

        public void Dispose() => _scope.Dispose();

        // ────────────────────────────────────────────────────────────────
        //  Reading the inventory out of source
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Matches the four-string FixedKeyEntry constructor rows:
        /// new("context", "label", "key", "description", ...).
        /// </summary>
        private static readonly Regex RowPattern = new(
            "new\\(\\s*\"(?<ctx>[^\"]+)\"\\s*,\\s*\"(?<label>[^\"]+)\"\\s*,\\s*\"(?<key>[^\"]+)\"\\s*,\\s*\\n?\\s*\"(?<desc>[^\"]+)\"",
            RegexOptions.Compiled | RegexOptions.Singleline);

        private static List<(string Key, string Description)> RowsFor(string context)
        {
            string source = ReadSource(Path.Combine("JJFlexWpf", "KeyInventory.cs"));
            return RowPattern.Matches(source)
                .Where(m => m.Groups["ctx"].Value == context)
                .Select(m => (m.Groups["key"].Value, m.Groups["desc"].Value))
                .ToList();
        }

        [Fact]
        public void The_row_parser_reads_a_known_sample()
        {
            // Positive control: a negative result from RowsFor also claims the
            // parser would have SEEN a row, so first make it find one we wrote.
            var rows = RowPattern.Matches(
                "new(\"Freq.Test\", \"Test field\", \"Up / Down\",\n" +
                "    \"Tune by something\",\n" +
                "    new[] { \"tune\" }),")
                .Select(m => (m.Groups["ctx"].Value, m.Groups["key"].Value, m.Groups["desc"].Value))
                .ToList();
            Assert.Single(rows);
            Assert.Equal(("Freq.Test", "Up / Down", "Tune by something"), rows[0]);
        }

        [Fact]
        public void The_classic_map_holds_its_eight_rows()
        {
            var rows = RowsFor("Freq.Classic");
            Assert.Equal(8, rows.Count);
            Assert.Equal("Up / Down", rows[0].Key);
        }

        [Fact]
        public void The_modern_map_holds_its_eight_rows()
        {
            var rows = RowsFor("Freq.Modern");
            Assert.Equal(8, rows.Count);
            Assert.Contains(rows, r => r.Key == "Shift+Up / Shift+Down");
        }

        /// <summary>
        /// The one rule #302 exists to teach: vertical tunes, horizontal
        /// sizes; plain is coarse, modified is fine. Four rows carry it, and
        /// they only teach it while all four are present and say what they
        /// say — a map missing the horizontal half teaches an operator that
        /// the step sizes are a Settings trip, which is what they were.
        /// </summary>
        [Fact]
        public void The_modern_map_teaches_both_halves_of_the_one_rule()
        {
            var rows = RowsFor("Freq.Modern");

            Assert.Contains(rows, r => r.Key == "Up / Down"
                && r.Description == "Tune by your coarse step");
            Assert.Contains(rows, r => r.Key == "Shift+Up / Shift+Down"
                && r.Description == "Tune by your fine step");
            Assert.Contains(rows, r => r.Key == "Alt+Left / Alt+Right"
                && r.Description == "Make your coarse step smaller or larger");
            Assert.Contains(rows, r => r.Key == "Shift+Left / Shift+Right"
                && r.Description == "Make your fine step smaller or larger");
            Assert.Contains(rows, r => r.Key == "S"
                && r.Description == "Choose both step sizes from a list");
        }

        /// <summary>
        /// Bare Left / Right must NOT appear in the Modern map. It is a
        /// HomeNav row — cursor movement across the whole Home surface, in
        /// both tuning modes — and Modern's sizing pairs deliberately carry a
        /// modifier to leave it alone. A bare row here would tell an operator
        /// the cursor keys resize their step, which they do not.
        /// </summary>
        [Fact]
        public void The_modern_map_leaves_the_bare_cursor_pair_to_home_navigation()
        {
            Assert.DoesNotContain(RowsFor("Freq.Modern"), r => r.Key == "Left / Right");
            Assert.Contains(RowsFor("HomeNav"), r => r.Key == "Left / Right");
        }

        [Fact]
        public void The_cursor_movement_row_still_exists_for_classic_to_borrow()
        {
            // KeyInventory.FrequencyContextRows prepends the HomeNav
            // "Left / Right" row in Classic, looked up by its key display.
            // Two things must hold or that lookup silently returns nothing:
            // the row exists, and the method still asks for it.
            var nav = RowsFor("HomeNav");
            Assert.Contains(nav, r => r.Key == "Left / Right");

            string source = ReadSource(Path.Combine("JJFlexWpf", "KeyInventory.cs"));
            Assert.Contains("FrequencyContextRows", source);
            int method = source.IndexOf("FrequencyContextRows", StringComparison.Ordinal);
            Assert.Contains("e.KeyDisplay == \"Left / Right\"",
                source.Substring(method));
        }

        /// <summary>
        /// The rows exactly as MainWindow hands them to the composer:
        /// Classic borrows the cursor-movement row up front, Modern does not.
        /// Mirrors KeyInventory.FrequencyContextRows, whose shape the test
        /// above pins to source.
        /// </summary>
        private static List<(string Key, string Description)> ContextRows(bool modern)
        {
            var rows = RowsFor(modern ? "Freq.Modern" : "Freq.Classic");
            if (!modern)
            {
                var lr = RowsFor("HomeNav").First(r => r.Key == "Left / Right");
                rows.Insert(0, lr);
            }
            return rows;
        }

        // ────────────────────────────────────────────────────────────────
        //  The assembled sentences — the review surface
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Classic_at_chatty_reads_state_then_the_live_map_then_the_way_out()
        {
            string text = TuningContextHelp.ComposeFrequencyField(
                modern: false, chatty: true, switchKeyDisplay: "Ctrl+Shift+M",
                coarseStep: "5 kilohertz", fineStep: "100 hertz",
                cursorStepName: "1 kilohertz", liveKeys: ContextRows(modern: false));

            Assert.Equal(
                "Classic tuning mode. Your cursor is on the 1 kilohertz digit. " +
                "Left / Right, Move one character at a time across the fields. " +
                "Up / Down, Tune by the digit under the cursor. " +
                "U / D, Tune up or down (same as Up and Down). " +
                "Digits, Type a frequency, then Enter to apply. " +
                "K, Round to the nearest kilohertz. " +
                "Plus then digits, Set a step multiplier for Up and Down. " +
                "F, Speak the current frequency. " +
                "S, Turn split on. " +
                "T, Toggle showing the transmit frequency. " +
                "Ctrl+Shift+M switches to Modern tuning. " +
                "Press question mark for every key on this field.",
                text);
        }

        [Fact]
        public void Classic_at_terse_keeps_the_whole_map_and_trims_only_ceremony()
        {
            string text = TuningContextHelp.ComposeFrequencyField(
                modern: false, chatty: false, switchKeyDisplay: "Ctrl+Shift+M",
                coarseStep: "5 kilohertz", fineStep: "100 hertz",
                cursorStepName: "1 kilohertz", liveKeys: ContextRows(modern: false));

            Assert.Equal(
                "Classic tuning. Cursor on the 1 kilohertz digit. " +
                "Left / Right, Move one character at a time across the fields. " +
                "Up / Down, Tune by the digit under the cursor. " +
                "U / D, Tune up or down (same as Up and Down). " +
                "Digits, Type a frequency, then Enter to apply. " +
                "K, Round to the nearest kilohertz. " +
                "Plus then digits, Set a step multiplier for Up and Down. " +
                "F, Speak the current frequency. " +
                "S, Turn split on. " +
                "T, Toggle showing the transmit frequency. " +
                "Ctrl+Shift+M for Modern tuning.",
                text);
        }

        [Fact]
        public void Modern_at_chatty_reads_the_step_values_not_just_the_words()
        {
            string text = TuningContextHelp.ComposeFrequencyField(
                modern: true, chatty: true, switchKeyDisplay: "Ctrl+Shift+M",
                coarseStep: "5 kilohertz", fineStep: "100 hertz",
                cursorStepName: null, liveKeys: ContextRows(modern: true));

            Assert.Equal(
                "Modern tuning mode. Coarse step 5 kilohertz, fine step 100 hertz. " +
                "Up / Down, Tune by your coarse step. " +
                "Shift+Up / Shift+Down, Tune by your fine step. " +
                "Alt+Left / Alt+Right, Make your coarse step smaller or larger. " +
                "Shift+Left / Shift+Right, Make your fine step smaller or larger. " +
                "Digits, Type a frequency, then Enter to apply. " +
                "F, Speak the current frequency. " +
                "S, Choose both step sizes from a list. " +
                "Shift+S, Speak the coarse and fine step sizes. " +
                "Ctrl+Shift+M switches to Classic tuning. " +
                "Press question mark for every key on this field.",
                text);
        }

        [Fact]
        public void Modern_at_terse_reads_the_step_values_not_just_the_words()
        {
            string text = TuningContextHelp.ComposeFrequencyField(
                modern: true, chatty: false, switchKeyDisplay: "Ctrl+Shift+M",
                coarseStep: "5 kilohertz", fineStep: "100 hertz",
                cursorStepName: null, liveKeys: ContextRows(modern: true));

            Assert.Equal(
                "Modern tuning. Coarse 5 kilohertz, fine 100 hertz. " +
                "Up / Down, Tune by your coarse step. " +
                "Shift+Up / Shift+Down, Tune by your fine step. " +
                "Alt+Left / Alt+Right, Make your coarse step smaller or larger. " +
                "Shift+Left / Shift+Right, Make your fine step smaller or larger. " +
                "Digits, Type a frequency, then Enter to apply. " +
                "F, Speak the current frequency. " +
                "S, Choose both step sizes from a list. " +
                "Shift+S, Speak the coarse and fine step sizes. " +
                "Ctrl+Shift+M for Classic tuning.",
                text);
        }

        // ────────────────────────────────────────────────────────────────
        //  Degraded inputs must degrade to true sentences
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void An_unbound_switch_key_offers_the_menu_instead_of_quoting_nothing()
        {
            string text = TuningContextHelp.ComposeFrequencyField(
                modern: true, chatty: false, switchKeyDisplay: null,
                coarseStep: "5 kilohertz", fineStep: "100 hertz",
                cursorStepName: null, liveKeys: ContextRows(modern: true));

            Assert.Contains(
                "The tuning mode switch is not bound to a key; the Slice menu switches to Classic tuning.",
                text);
            Assert.DoesNotContain("not bound switches", text);
        }

        [Fact]
        public void A_missing_cursor_name_drops_the_sentence_not_the_grammar()
        {
            string text = TuningContextHelp.ComposeFrequencyField(
                modern: false, chatty: true, switchKeyDisplay: "Ctrl+Shift+M",
                coarseStep: "5 kilohertz", fineStep: "100 hertz",
                cursorStepName: null, liveKeys: ContextRows(modern: false));

            Assert.StartsWith("Classic tuning mode. Left / Right,", text);
            Assert.DoesNotContain("{digit}", text);
        }

        // ────────────────────────────────────────────────────────────────
        //  The doc stays in agreement (#274's promise)
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Every inventory row of the two Frequency maps must be visible in
        /// its keyboard-reference.md section. The Modern map has drifted from
        /// its spoken copy once already (#274, the deleted 'C' key taught for
        /// five sprints); context help now composes from the inventory at
        /// runtime, and this pin makes the remaining hand-written rendering —
        /// the doc — fail a build instead of failing an operator.
        /// </summary>
        [Theory]
        [InlineData("Freq.Classic", "## JJ Flexible Home — Frequency Field Keys (Classic tuning mode)")]
        [InlineData("Freq.Modern", "## JJ Flexible Home — Frequency Field Keys (Modern tuning mode)")]
        public void The_keyboard_reference_shows_every_row_of_the_live_map(
            string context, string heading)
        {
            string doc = ReadSource(Path.Combine("docs", "help", "md", "keyboard-reference.md"));
            int start = doc.IndexOf(heading, StringComparison.Ordinal);
            Assert.True(start >= 0, "section heading not found: " + heading);
            int end = doc.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);
            string section = Normalize(end < 0 ? doc.Substring(start) : doc.Substring(start, end - start));

            foreach (var (key, description) in RowsFor(context))
            {
                Assert.True(
                    ContainsAnyFragment(section, description),
                    $"the doc section for {context} does not mention the action of '{key}' " +
                    $"(\"{description}\") — update keyboard-reference.md or the inventory, " +
                    "whichever is now wrong");
                foreach (string token in key.Split('/', StringSplitOptions.TrimEntries))
                {
                    Assert.True(section.Contains(Normalize(token), StringComparison.Ordinal),
                        $"the doc section for {context} does not mention the key '{token}'");
                }
            }
        }

        /// <summary>
        /// True when the section carries the description, one of its
        /// parenthesized asides, or its first or last four words — enough
        /// slack for the doc's fuller phrasings ("Tune up by your coarse
        /// step") without letting a genuinely absent action pass.
        /// </summary>
        private static bool ContainsAnyFragment(string normalizedSection, string description)
        {
            var candidates = new List<string> { description };
            foreach (Match m in Regex.Matches(description, "\\(([^)]+)\\)"))
                candidates.Add(m.Groups[1].Value);
            string bare = Regex.Replace(description, "\\([^)]*\\)", " ");
            var words = bare.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 4)
            {
                candidates.Add(string.Join(' ', words.Take(4)));
                candidates.Add(string.Join(' ', words.Skip(words.Length - 4)));
            }
            return candidates.Any(c =>
                normalizedSection.Contains(Normalize(c), StringComparison.Ordinal));
        }

        /// <summary>Lowercase, "+" spelled out, punctuation gone, spaces collapsed.</summary>
        private static string Normalize(string text)
        {
            string t = text.ToLowerInvariant().Replace("+", " plus ");
            t = Regex.Replace(t, "[^a-z0-9 ]", " ");
            return Regex.Replace(t, "\\s+", " ").Trim();
        }

        // ────────────────────────────────────────────────────────────────
        //  Plumbing
        // ────────────────────────────────────────────────────────────────

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
