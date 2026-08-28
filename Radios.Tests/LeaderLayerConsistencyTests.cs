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
            Assert.Equal("Toggle a thing", entries[0].Description);
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
            Assert.Contains("leader.cancelled", src);
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
        //
        //  The scanners themselves live in LeaderSourceScan, shared with the
        //  doc-coverage check (#265). They were extracted from here rather
        //  than copied: two readers of one table is the defect both tests
        //  exist to catch.
        // ────────────────────────────────────────────────────────────────

        private static HashSet<Keys> RealAdvertised(out int entryCount)
            => LeaderSourceScan.RealAdvertised(out entryCount);

        private static HashSet<Keys> RealHandled()
            => LeaderSourceScan.RealHandled();

        private static HashSet<Keys> Advertised(List<LeaderSourceScan.InventoryEntry> entries)
            => LeaderSourceScan.Advertised(entries);

        private static List<LeaderSourceScan.InventoryEntry> InventoryEntries(string source)
            => LeaderSourceScan.InventoryEntries(source);

        private static HashSet<Keys> SwitchCases(string source, string methodName)
            => LeaderSourceScan.SwitchCases(source, methodName);

        private static string ReadSource(string relative)
            => LeaderSourceScan.ReadSource(relative);
    }
}
