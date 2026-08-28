using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Compares the leader table the app GENERATES against the two copies of it
    /// maintained by hand in the help pages (#265), both directions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> <c>KeyInventory.LeaderCommands</c> is already the
    /// single definition behind five consumers: the Keys dialog, the Command
    /// Finder rows, the exported key list, the key manifest, and the spoken
    /// Ctrl+J, H help. Beside it, <c>keyboard-reference.md</c> and
    /// <c>leader-key.md</c> each carry a SECOND and THIRD hand-typed copy of the
    /// same table, and on 2026-08-26 all three disagreed: the reference had no
    /// row for Ctrl+J, Ctrl+R, which had shipped since Sprint 31, and
    /// leader-key.md was missing Ctrl+D, Ctrl+R and E.
    /// </para>
    /// <para>
    /// <b>That is not untidiness, it is the failure this project exists to
    /// avoid.</b> A chord that works perfectly and appears in no reference is
    /// undiscoverable — BlindCat anti-pattern #1. The code is right, the tests
    /// pass, the key works, and the operator never learns it is there.
    /// </para>
    /// <para>
    /// <b>Why check rather than generate.</b> Generating these tables from the
    /// inventory would make the pages correct and worse. The doc rows carry
    /// prose the inventory deliberately does not — cross-references to the
    /// sections below, the reason Shift+F is reserved, what happens if the
    /// capture cannot find a sound device — while the inventory description has
    /// to stay short enough to be one line of the spoken Ctrl+J, H help.
    /// Generating would flatten thirty rows of Noel's help writing to fix an
    /// omission bug. So the inventory stays the single source of truth about
    /// WHICH CHORDS EXIST, the pages keep their own words about what each one
    /// does, and this test makes an omission a red build instead of a silence.
    /// </para>
    /// <para>
    /// The checked region is delimited by HTML comments in each page, so the
    /// test never has to guess where the leader list starts and a future list
    /// on the same page cannot confuse it. The comments are invisible in the
    /// built CHM.
    /// </para>
    /// <para>
    /// <b>The pages stopped being tables in Sprint 37 Track K (#289), and this
    /// reader changed with them.</b> Both pages presented their key lists as
    /// markdown tables — the one document format this project's own convention
    /// forbids, on the page a blind operator is most likely to be reading
    /// BECAUSE they are stuck. They are now one bullet per chord, chord
    /// leading:
    /// </para>
    /// <code>
    /// - **Shift+N** — Toggle NR Filter
    /// </code>
    /// <para>
    /// <b>The reader is deliberately strict about that shape, and refuses the
    /// old one outright.</b> Accepting both would have been the softer change
    /// and the wrong one: a reader that quietly takes several shapes is a
    /// reader nobody notices going blind, and a scanner that sees nothing
    /// reports PERFECT AGREEMENT — which reads exactly like success. So a row
    /// written any other way is invisible here, its chord reads as
    /// undocumented, and the build goes red naming it.
    /// <see cref="Neither_keyboard_page_contains_a_markdown_table"/> closes the
    /// same gap from the other side, so a re-added table is a red build with an
    /// explanation rather than a silently unread region.
    /// </para>
    /// <para>
    /// The marker text still says TABLE. That is now a slight misnomer and is
    /// left alone on purpose — the name is quoted in the task register and in
    /// several sprint briefs, and renaming a landmark two tests depend on, to
    /// gain accuracy in a comment, is not a trade worth making mid-sprint.
    /// </para>
    /// </remarks>
    public class LeaderDocCoverageTests
    {
        private const string ReferencePage = "docs/help/md/keyboard-reference.md";
        private const string LeaderPage = "docs/help/md/leader-key.md";

        private const string RegionStart = "<!-- LEADER-KEY-TABLE";
        private const string RegionEnd = "<!-- END LEADER-KEY-TABLE";

        /// <summary>
        /// One documented chord: a bullet, the chord in bold, an em dash, the
        /// meaning. The chord itself never contains an asterisk, so the greedy
        /// class stops at the closing marker and an em dash inside the MEANING
        /// — of which there are many — cannot confuse it.
        /// </summary>
        private static readonly Regex EntryLine =
            new(@"^[-*]\s+\*\*([^*]+)\*\*\s+—\s", RegexOptions.Compiled);

        public static IEnumerable<object[]> Pages =>
            new[] { new object[] { ReferencePage }, new object[] { LeaderPage } };

        // ────────────────────────────────────────────────────────────────
        //  Prove the instruments before trusting their silence
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void The_entry_reader_takes_chords_and_skips_headings_prose_and_anything_outside()
        {
            const string sample = @"
Some prose about the layer.

<!-- LEADER-KEY-TABLE: the shape, shown so an author can copy it:

         - **Shift+Q** — an EXAMPLE inside the marker comment, never a real entry
-->

### A group heading

Some prose inside the region, which is allowed and must not be read.

- **N** — Toggle a thing
- **Shift+N** — Toggle another thing
- **Ctrl+A** — Turn a thing on or off — with an em dash in the meaning too
- A bullet with no bold chord, which is not an entry
- **Bold prose** that never reaches an em dash, which is not an entry either

<!-- END LEADER-KEY-TABLE -->

- **Z** — A bullet OUTSIDE the region, which must not be read
";
            var entries = KeyEntries(sample);

            Assert.Equal(new[] { "N", "Shift+N", "Ctrl+A" }, entries);
        }

        [Fact]
        public void The_entry_reader_does_not_read_a_row_written_in_the_old_table_shape()
        {
            // Sprint 37 Track K converted both pages off markdown tables. This
            // states the consequence out loud: a table row is now INVISIBLE
            // here. That is the safe direction — the chord reads as
            // undocumented and the build goes red naming it — but only because
            // Neither_keyboard_page_contains_a_markdown_table catches the same
            // mistake from the other side and says what the shape should be.
            const string sample = @"
<!-- LEADER-KEY-TABLE -->

| Key | Action |
|-----|--------|
| N | Toggle a thing |

- **B** — Toggle a thing that IS written in the new shape

<!-- END LEADER-KEY-TABLE -->
";
            Assert.Equal(new[] { "B" }, KeyEntries(sample));
        }

        [Fact]
        public void The_entry_reader_reads_nothing_when_the_markers_are_missing()
        {
            // The failure this whole file is built around: a reader that goes
            // blind reports perfect agreement. Prove it returns EMPTY rather
            // than something plausible, so the >= 30 guard below is the thing
            // standing between a renamed marker and a vacuous pass.
            const string sample = @"
- **N** — Toggle a thing, with no region markers anywhere on the page
";
            Assert.Empty(KeyEntries(sample));
        }

        [Fact]
        public void A_planted_missing_row_is_reported()
        {
            // The positive control this project insists on: a check whose
            // silence has never been tested is decorative.
            var advertised = new Dictionary<Keys, string>
            {
                [Keys.N] = "Ctrl+J, N",
                [Keys.D | Keys.Control] = "Ctrl+J, Ctrl+D",
            };
            var documented = new HashSet<Keys> { Keys.N };

            var missing = advertised.Where(kv => !documented.Contains(kv.Key))
                                    .Select(kv => kv.Value).ToList();

            Assert.Equal(new[] { "Ctrl+J, Ctrl+D" }, missing);
        }

        [Fact]
        public void A_planted_row_for_a_chord_nobody_binds_is_reported()
        {
            var advertised = new HashSet<Keys> { Keys.N };
            var documented = new HashSet<Keys> { Keys.N, Keys.Z };

            Assert.Equal(new[] { Keys.Z }, documented.Except(advertised).ToArray());
        }

        // ────────────────────────────────────────────────────────────────
        //  The sweep — the real inventory against the real pages
        // ────────────────────────────────────────────────────────────────

        [Theory]
        [MemberData(nameof(Pages))]
        public void Every_leader_chord_has_a_row_on_this_page(string page)
        {
            var advertised = AdvertisedByChord();
            var documented = DocumentedChords(page);

            var missing = advertised.Where(kv => !documented.Contains(kv.Key))
                                    .Select(kv => kv.Value)
                                    .Distinct()
                                    .OrderBy(s => s, StringComparer.Ordinal)
                                    .ToList();

            Assert.True(missing.Count == 0,
                "Advertised in KeyInventory.LeaderCommands and absent from " + page + ": "
                + string.Join(", ", missing)
                + ". A chord with no line in the help is one the operator cannot discover, "
                + "which is the whole reason this check exists. Add a line inside the "
                + "LEADER-KEY-TABLE region, in that page's own voice and in the page's "
                + "shape — a hyphen, the chord in bold, an em dash, the meaning: "
                + "`- **Shift+N** — Toggle NR Filter`. If you believe the line is already "
                + "there, it is written in some other shape and the reader cannot see it.");
        }

        [Theory]
        [MemberData(nameof(Pages))]
        public void Every_row_on_this_page_is_a_chord_the_layer_advertises(string page)
        {
            var advertised = AdvertisedByChord();
            var documented = DocumentedChords(page);

            var phantom = documented.Except(advertised.Keys).OrderBy(k => k).ToList();

            Assert.True(phantom.Count == 0,
                page + " documents chords KeyInventory.LeaderCommands does not advertise: "
                + string.Join(", ", phantom)
                + ". Either the line is stale and describes a chord that was removed, or the "
                + "chord is real and the inventory is the thing missing it — in which case "
                + "the Keys dialog, the Command Finder and the Ctrl+J, H help are all missing "
                + "it too.");
        }

        [Theory]
        [MemberData(nameof(Pages))]
        public void The_sweep_actually_found_the_list(string page)
        {
            // Without this, a renamed marker or a moved file would empty both
            // sets and make every check above pass vacuously — silent success,
            // this project's favourite failure.
            var documented = DocumentedChords(page);
            Assert.True(documented.Count >= 30,
                $"only {documented.Count} chords read out of {page} — the LEADER-KEY-TABLE "
                + "region markers are missing, renamed, or no longer wrap the list, or the "
                + "key lines have drifted off the shape the reader takes: a hyphen, the chord "
                + "in bold, an em dash, the meaning — `- **Shift+N** — Toggle NR Filter`");
        }

        [Theory]
        [MemberData(nameof(Pages))]
        public void Neither_keyboard_page_contains_a_markdown_table(string page)
        {
            // #289. These two pages are the reference a blind operator reaches
            // for BECAUSE they are stuck, and until Sprint 37 they presented
            // every key list as a markdown table — the one document format this
            // project's convention forbids, on the page that most needed to
            // obey it. Converting them is only half the fix; without this, the
            // next person adding a key adds a table row, because a table row is
            // what the page used to look like and nothing would say otherwise.
            //
            // It is checked page-wide rather than inside the markers on
            // purpose: the convention is about the whole document, and the
            // reader above cannot see a table row at all, so inside the region
            // this is the ONLY thing that can report one.
            string text = File.ReadAllText(Path.Combine(LeaderSourceScan.RepoRoot(), page));
            var tableLines = TableLines(text);

            Assert.True(tableLines.Count == 0,
                page + " has " + tableLines.Count + " markdown table line(s), starting with: "
                + (tableLines.Count > 0 ? tableLines[0] : "")
                + ". This project's help convention is prose or bullets, never tables — screen "
                + "readers cannot navigate a table comfortably, and this is the page someone "
                + "reads when they are already stuck. One line per key, chord leading: "
                + "`- **Shift+N** — Toggle NR Filter`. Inside the LEADER-KEY-TABLE region a "
                + "table row is worse than untidy: the coverage reader cannot see it, so the "
                + "chord would read as undocumented.");
        }

        [Fact]
        public void Both_pages_document_the_same_set_of_chords()
        {
            // The two pages are independent copies, so they can drift from each
            // other as well as from the inventory. In practice the checks above
            // already force both to equal the inventory; this states the
            // consequence directly, so a failure reads as "the pages disagree"
            // rather than as two separate coincidences.
            var reference = DocumentedChords(ReferencePage);
            var leader = DocumentedChords(LeaderPage);

            Assert.Equal(
                reference.OrderBy(k => k).ToArray(),
                leader.OrderBy(k => k).ToArray());
        }

        // ────────────────────────────────────────────────────────────────
        //  Extraction
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Every advertised chord, mapped to the inventory display string that
        /// advertised it — so a failure can name the row to add without this
        /// test growing a second key formatter to say "Ctrl+D" with.
        /// </summary>
        private static Dictionary<Keys, string> AdvertisedByChord()
        {
            var map = new Dictionary<Keys, string>();
            foreach (var e in LeaderSourceScan.RealInventoryEntries())
            {
                foreach (var chord in LeaderChordParser.ParseDisplay(e.Display, e.Excluded))
                {
                    if (!map.ContainsKey(chord)) map[chord] = e.Display;
                }
            }
            Assert.True(map.Count >= 30,
                $"only {map.Count} advertised chords parsed — the inventory reader is broken, "
                + "and every comparison below would pass on an empty set");
            return map;
        }

        private static HashSet<Keys> DocumentedChords(string page)
        {
            // Range rows on a page carry the same gaps the inventory declares —
            // "Shift+A through Shift+H" reads Shift+F as part of the range on
            // both sides, and Shift+F is the RX filter readout. Taking the
            // exclusions FROM the inventory rather than restating them here
            // keeps this test from becoming a fourth copy of the table.
            var exclusions = LeaderSourceScan.RealInventoryEntries()
                .SelectMany(e => e.Excluded).Distinct().ToList();

            string text = File.ReadAllText(Path.Combine(LeaderSourceScan.RepoRoot(), page));
            var chords = new HashSet<Keys>();

            foreach (string entry in KeyEntries(text))
            {
                bool isRange = entry.Contains(" through ", StringComparison.Ordinal);
                foreach (var c in LeaderChordParser.ParseDisplay(entry, isRange ? exclusions : null))
                    chords.Add(c);
            }
            return chords;
        }

        /// <summary>
        /// The chord named by every key line inside the page's LEADER-KEY-TABLE
        /// region. Group headings, prose and anything outside the markers are
        /// excluded — and so is the text of the opening marker comment itself,
        /// which carries a worked example of the shape and would otherwise be
        /// read as a real entry.
        /// </summary>
        private static List<string> KeyEntries(string text)
        {
            var entries = new List<string>();

            int marker = text.IndexOf(RegionStart, StringComparison.Ordinal);
            if (marker < 0) return entries;

            // Start AFTER the opening comment closes, not at the marker. The
            // comment documents the required shape by showing one, and a reader
            // that started at the marker would count that example as a chord.
            int commentEnd = text.IndexOf("-->", marker, StringComparison.Ordinal);
            if (commentEnd < 0) return entries;

            int start = commentEnd + 3;
            int end = text.IndexOf(RegionEnd, start, StringComparison.Ordinal);
            if (end < 0) return entries;

            string region = text.Substring(start, end - start);

            foreach (string raw in region.Split('\n'))
            {
                var m = EntryLine.Match(raw.Trim());
                if (!m.Success) continue;

                string chord = m.Groups[1].Value.Trim().Trim('`').Trim();
                if (chord.Length > 0) entries.Add(chord);
            }
            return entries;
        }

        /// <summary>Every line of a page's checked region that looks like a markdown table.</summary>
        private static List<string> TableLines(string text)
        {
            var lines = new List<string>();
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("|", StringComparison.Ordinal)) lines.Add(line);
            }
            return lines;
        }
    }
}
