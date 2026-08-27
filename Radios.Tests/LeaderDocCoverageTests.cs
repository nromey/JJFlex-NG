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
    /// test never has to guess where the leader table starts and a future
    /// non-leader table on the same page cannot confuse it. The comments are
    /// invisible in the built CHM.
    /// </para>
    /// </remarks>
    public class LeaderDocCoverageTests
    {
        private const string ReferencePage = "docs/help/md/keyboard-reference.md";
        private const string LeaderPage = "docs/help/md/leader-key.md";

        private const string RegionStart = "<!-- LEADER-KEY-TABLE";
        private const string RegionEnd = "<!-- END LEADER-KEY-TABLE";

        public static IEnumerable<object[]> Pages =>
            new[] { new object[] { ReferencePage }, new object[] { LeaderPage } };

        // ────────────────────────────────────────────────────────────────
        //  Prove the instruments before trusting their silence
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void The_table_reader_takes_key_cells_and_skips_headers_and_rules()
        {
            const string sample = @"
Some prose about the layer.

<!-- LEADER-KEY-TABLE -->

| Key | Action |
|-----|--------|
| N | Toggle a thing |
| `Shift+N` | Toggle another thing |
| Ctrl+A | Turn a thing on or off — with an em dash | in the middle |

<!-- END LEADER-KEY-TABLE -->

| Key | Action |
|-----|--------|
| Z | A row OUTSIDE the region, which must not be read |
";
            var cells = KeyCells(sample);

            Assert.Equal(new[] { "N", "Shift+N", "Ctrl+A" }, cells);
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
                + ". A chord with no row in the help is one the operator cannot discover, "
                + "which is the whole reason this check exists. Add a row inside the "
                + "LEADER-KEY-TABLE region, in that page's own voice.");
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
                + ". Either the row is stale and describes a chord that was removed, or the "
                + "chord is real and the inventory is the thing missing it — in which case "
                + "the Keys dialog, the Command Finder and the Ctrl+J, H help are all missing "
                + "it too.");
        }

        [Theory]
        [MemberData(nameof(Pages))]
        public void The_sweep_actually_found_the_table(string page)
        {
            // Without this, a renamed marker or a moved file would empty both
            // sets and make every check above pass vacuously — silent success,
            // this project's favourite failure.
            var documented = DocumentedChords(page);
            Assert.True(documented.Count >= 30,
                $"only {documented.Count} chords read out of {page} — the LEADER-KEY-TABLE "
                + "region markers are missing, renamed, or no longer wrap the table");
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

            foreach (string cell in KeyCells(text))
            {
                bool isRange = cell.Contains(" through ", StringComparison.Ordinal);
                foreach (var c in LeaderChordParser.ParseDisplay(cell, isRange ? exclusions : null))
                    chords.Add(c);
            }
            return chords;
        }

        /// <summary>
        /// The first column of every table row inside the page's
        /// LEADER-KEY-TABLE region: header rows, separator rules and anything
        /// outside the markers excluded.
        /// </summary>
        private static List<string> KeyCells(string text)
        {
            var cells = new List<string>();

            int start = text.IndexOf(RegionStart, StringComparison.Ordinal);
            int end = text.IndexOf(RegionEnd, StringComparison.Ordinal);
            if (start < 0 || end < 0 || end < start) return cells;

            string region = text.Substring(start, end - start);

            foreach (string raw in region.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length < 3 || line[0] != '|') continue;

                var parts = line.Split('|');
                if (parts.Length < 3) continue;

                string cell = parts[1].Trim().Trim('`').Trim();
                if (cell.Length == 0) continue;
                if (string.Equals(cell, "Key", StringComparison.OrdinalIgnoreCase)) continue;
                if (Regex.IsMatch(cell, @"^:?-{2,}:?$")) continue;

                cells.Add(cell);
            }
            return cells;
        }
    }
}
