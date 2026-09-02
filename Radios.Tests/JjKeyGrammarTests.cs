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
    /// Holds the JJ key's switch to the four-tier grammar (#515): what a chord
    /// MEANS, tier by tier, rather than whether it exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the existing checks could not catch #504.</b>
    /// <see cref="LeaderLayerConsistencyTests"/> compares the chords the switch
    /// handles against the chords the inventory advertises, and
    /// <see cref="LeaderDocCoverageTests"/> compares the inventory against both
    /// help pages — all four directions, and every one of them passed while
    /// slice F was unreachable. They compare SETS. Shift+F was in every set:
    /// the switch handled it, the inventory advertised it, both pages
    /// documented it. It was the RX filter readout in all four places, and only
    /// the comment above the slice row — "Shift+A through Shift+H" — said
    /// otherwise. A set check cannot see a comment lie, and it cannot see that
    /// a row which claims to be complete is missing a member whose chord is
    /// bound to something else.
    /// </para>
    /// <para>
    /// <b>The grammar is the claim that can be checked.</b> Noel's ruling
    /// (#515, 2026-09-02): a plain letter OPENS A LAYER; Shift plus a letter
    /// JUMPS TO THAT SLICE, from anywhere, in any layer; Ctrl plus a letter
    /// TOGGLES; Alt is everything else. Each tier has one meaning, so each tier
    /// can be read out of the switch's arms and held to it — a Shift+letter arm
    /// that is not a slice jump is a violation, not a style choice. That is the
    /// check that would have gone red on the day the slice row was written
    /// without F.
    /// </para>
    /// <para>
    /// <b>The debt lists are the honest part.</b> The plain-letter one-shots
    /// predate the grammar and keep working until Noel walks each letter, one
    /// at a time. They are recorded here with a reason, and the list is checked
    /// in BOTH directions: an entry whose chord has stopped being a one-shot is
    /// stale and fails, and a one-shot that is not on the list is undeclared and
    /// fails. So the list can only shrink, and it shrinks by somebody deleting
    /// a line here on the day the letter moves — the same discipline
    /// <c>_unboundNotes</c> already applies to the registry.
    /// </para>
    /// <para>
    /// It reads SOURCE, in the <see cref="LeaderSourceScan"/> family, because
    /// Radios.Tests cannot load the WPF assembly and because the thing being
    /// verified is literal code written by people. A layer entry is recognised
    /// by its call — <c>EnterVolumeMode()</c>, <c>EnterPanMode()</c>, and
    /// whatever <c>Enter…Layer()</c> the filter and audio layers publish; a
    /// layer whose entry is named some other way fails here by name, which is
    /// the right failure: the merge then chooses the name or widens the
    /// pattern, out loud.
    /// </para>
    /// </remarks>
    public class JjKeyGrammarTests
    {
        // ────────────────────────────────────────────────────────────────
        //  The recorded debt — every line is a letter Noel has not walked
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Plain letters that still carry a one-shot command. Under the grammar
        /// a plain letter opens a layer; these predate it. Delete a line the
        /// day its letter moves — the check below fails if the letter is still
        /// a one-shot with no line here, and if a line is here for a letter
        /// that is no longer a one-shot.
        /// </summary>
        private static readonly Dictionary<Keys, string> PlainTierDebt = new()
        {
            [Keys.B] = "Noise Blanker toggle — the noise layer's, whose letter is not yet ruled",
            [Keys.C] = "Compander toggle — a transmit toggle; Ctrl+C is the copy chord",
            [Keys.D] = "tuning speech debounce toggle",
            [Keys.E] = "echo recent CW — an action, so the Alt tier's under the grammar",
            [Keys.G] = "TX test tone arm/disarm — a toggle",
            [Keys.K] = "mic check — an action, so the Alt tier's under the grammar",
            [Keys.L] = "log statistics — an action, so the Alt tier's under the grammar",
            [Keys.M] = "memories dialog — M is ruled for the mode layer (#515), which is not yet built",
            [Keys.N] = "legacy Noise Reduction toggle — the noise layer's, whose letter is not yet ruled",
            [Keys.O] = "what is on — an action, so the Alt tier's under the grammar",
            [Keys.P] = "Audio Peak Filter toggle — the noise layer's; Ctrl+P is PC audio (#513)",
            [Keys.Q] = "capture a noise profile — an action, so the Alt tier's under the grammar",
            [Keys.R] = "On-Radio Neural NR toggle — the noise layer's; Ctrl+R is recorded problems",
            [Keys.S] = "On-Radio Spectral NR toggle — the noise layer's; Ctrl+S is the S-meter unit",
            [Keys.T] = "meter tones toggle",
            [Keys.W] = "Wideband Noise Blanker toggle — the noise layer's, whose letter is not yet ruled",
        };

        /// <summary>
        /// Shift+letter chords that are not slice jumps. Under the grammar
        /// Shift means jump to that slice and nothing else; these are the
        /// "other one of the pair" toggles from before it. Same two-way
        /// discipline as <see cref="PlainTierDebt"/>.
        /// </summary>
        private static readonly Dictionary<Keys, string> ShiftTierDebt = new()
        {
            [Keys.N | Keys.Shift] = "NR Filter toggle — the noise layer's",
            [Keys.P | Keys.Shift] = "Speech Processor toggle — a transmit toggle",
            [Keys.R | Keys.Shift] = "PC Neural NR toggle — the noise layer's",
            [Keys.S | Keys.Shift] = "PC Spectral NR toggle — the noise layer's",
            [Keys.T | Keys.Shift] = "alert sounds (earcons) toggle",
        };

        /// <summary>
        /// Alt+letter chords that open a layer. Alt is the leftovers tier —
        /// actions that are neither a layer nor a toggle — so a layer here is
        /// debt. Pan mode is merging into the audio layer (#514).
        /// </summary>
        private static readonly Dictionary<Keys, string> AltTierLayerDebt = new()
        {
            [Keys.P | Keys.Alt] = "pan mode — merges into the audio layer, JJ key A (#514)",
        };

        /// <summary>
        /// Plain letters RULED for layers whose layer is being built. Neither
        /// may carry a one-shot again, ever: a one-shot re-appearing on one of
        /// these is the exact regression the grammar forbids.
        /// </summary>
        private static readonly Dictionary<Keys, string> RuledLayerLetters = new()
        {
            [Keys.A] = "the audio layer (#514, #515)",
            [Keys.F] = "the filter layer (#512, #516)",
        };

        private static readonly Regex LayerEntry = new(@"\bEnter\w*(?:Mode|Layer)\s*\(", RegexOptions.Compiled);
        private static readonly Regex SliceJump = new(@"\bJumpToSlice\s*\(\s*(\d+)\s*\)", RegexOptions.Compiled);

        // ────────────────────────────────────────────────────────────────
        //  Prove the instruments before trusting their silence
        // ────────────────────────────────────────────────────────────────

        private const string Sample = @"
            private bool DoLeaderCommand(Keys k)
            {
                var rig = Get();
                switch (k)
                {
                    case Keys.N:
                        if (rig == null) NoRadio();
                        else Toggle(""x"", () => rig.A, v => rig.A = v);
                        break;
                    case Keys.V:
                        EnterVolumeMode();
                        break;
                    case Keys.Q | Keys.Control:
                        {
                            // a nested block with a nested switch, which must not split the arm
                            switch (rig.Mode) { case ""CW"": Cw(); break; default: Other(); break; }
                        }
                        break;
                    case Keys.Oem2:
                    case Keys.Oem2 | Keys.Shift:
                        OpenKeyExplorer();
                        break;
                    case Keys.H:
                        LeaderKeyHelp();
                        break;
                    case Keys.A | Keys.Shift: JumpToSlice(0); break;
                    case Keys.B | Keys.Shift: JumpToSlice(1); break;
                    case Keys.C | Keys.Shift: JumpToSlice(2); break;
                    case Keys.D | Keys.Shift: JumpToSlice(3); break;
                    case Keys.E | Keys.Shift: JumpToSlice(4); break;
                    case Keys.G | Keys.Shift: JumpToSlice(6); break;
                    case Keys.H | Keys.Shift: JumpToSlice(7); break;
                    default:
                        Unknown();
                        break;
                }
                return true;
            }";

        [Fact]
        public void The_arm_scanner_groups_labels_and_keeps_each_body_whole()
        {
            var arms = LeaderSourceScan.SwitchArms(Sample, "DoLeaderCommand");

            // Twelve arms: N, V, Ctrl+Q, the two-label slash arm, H, and seven
            // slice jumps. The default arm is not one.
            Assert.Equal(12, arms.Count);

            var slash = arms.Single(a => a.Labels.Contains(Keys.Oem2));
            Assert.Equal(new[] { Keys.Oem2, Keys.Oem2 | Keys.Shift }, slash.Labels);
            Assert.Contains("OpenKeyExplorer(", slash.Body);

            // The nested switch stayed INSIDE the Ctrl+Q arm rather than
            // splitting it, and its labels were not read as this switch's.
            var q = arms.Single(a => a.Labels.Contains(Keys.Q | Keys.Control));
            Assert.Contains("Other()", q.Body);
            Assert.DoesNotContain(arms, a => a.Labels.Count == 0);

            Assert.True(LayerEntry.IsMatch(arms.Single(a => a.Labels.Contains(Keys.V)).Body));
            Assert.False(LayerEntry.IsMatch(arms.Single(a => a.Labels.Contains(Keys.N)).Body));
        }

        [Fact]
        public void A_planted_gap_in_the_slice_row_is_reported_by_letter()
        {
            // The positive control: the sample above IS the pre-Sprint-44 row,
            // A to H without F. The check must name F, and nothing else.
            var arms = LeaderSourceScan.SwitchArms(Sample, "DoLeaderCommand");

            Assert.Equal(new[] { "Shift+F" }, SliceRowFaults(arms));
        }

        [Fact]
        public void A_planted_slice_arm_with_the_wrong_index_is_reported()
        {
            // The letter IS the index. A row that binds Shift+F to slice G's
            // index would pass a presence check and jump to the wrong slice.
            string wrong = Sample.Replace("case Keys.G | Keys.Shift: JumpToSlice(6); break;",
                                          "case Keys.F | Keys.Shift: JumpToSlice(6); break;\n"
                                        + "case Keys.G | Keys.Shift: JumpToSlice(6); break;");
            var arms = LeaderSourceScan.SwitchArms(wrong, "DoLeaderCommand");

            Assert.Contains("Shift+F", SliceRowFaults(arms).Single());
        }

        [Fact]
        public void A_planted_plain_letter_one_shot_outside_the_debt_list_is_reported()
        {
            var arms = LeaderSourceScan.SwitchArms(Sample, "DoLeaderCommand");
            var debt = new Dictionary<Keys, string>();   // nothing declared

            var undeclared = UndeclaredOneShots(arms, Keys.None, debt, exempt: new[] { Keys.H });

            Assert.Equal(new[] { Keys.N }, undeclared);   // V opens a layer; H is help
        }

        [Fact]
        public void A_planted_stale_debt_entry_is_reported()
        {
            var arms = LeaderSourceScan.SwitchArms(Sample, "DoLeaderCommand");
            var debt = new Dictionary<Keys, string> { [Keys.N] = "real", [Keys.Z] = "stale" };

            Assert.Equal(new[] { Keys.Z }, StaleDebt(arms, Keys.None, debt));
        }

        // ────────────────────────────────────────────────────────────────
        //  The Shift tier — jump to that slice, all eight, and nothing else
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Every_slice_letter_A_to_H_jumps_to_its_own_index()
        {
            // #504. The row claims A through H; the letter is the radio's
            // slice index; so every one of the eight must be present, alone
            // on its arm, and bound to its own index. Only a six-slice radio —
            // a 6700 or 6700R — can reach F, so no bench session here will
            // ever press it: this check is the only thing standing in for
            // that radio.
            var faults = SliceRowFaults(LeaderSourceScan.RealSwitchArms());

            Assert.True(faults.Count == 0,
                "The slice row (JJ key Shift+A through Shift+H) is not what it claims: "
                + string.Join("; ", faults)
                + ". Shift+<letter> is that slice, from anywhere, and means nothing else (#515). "
                + "If a Shift+letter chord was needed for something else, that something else "
                + "belongs on another tier — this row is complete by construction.");
        }

        [Fact]
        public void Shift_plus_a_letter_means_jump_to_that_slice_beyond_the_recorded_debt()
        {
            var arms = LeaderSourceScan.RealSwitchArms();

            var undeclared = arms
                .Where(a => a.Labels.Any(l => IsTier(l, Keys.Shift)) && !SliceJump.IsMatch(a.Body))
                .SelectMany(a => a.Labels.Where(l => IsTier(l, Keys.Shift)))
                .Where(l => !ShiftTierDebt.ContainsKey(l))
                .Select(Name).OrderBy(s => s, StringComparer.Ordinal).ToList();

            Assert.True(undeclared.Count == 0,
                "Shift+letter chords that are not slice jumps and are not recorded as debt: "
                + string.Join(", ", undeclared)
                + ". Under #515 the Shift tier means jump to that slice and nothing else — this "
                + "is the exact collision that made slice F unreachable (#504). Put the command "
                + "on the Ctrl tier if it toggles, the Alt tier if it acts, or inside a layer.");

            var stale = StaleDebt(arms, Keys.Shift, ShiftTierDebt,
                isOneShot: a => !SliceJump.IsMatch(a.Body));
            Assert.True(stale.Count == 0,
                "ShiftTierDebt lists chords that are no longer non-slice one-shots — delete "
                + "their lines, the debt is paid: " + string.Join(", ", stale.Select(Name)));
        }

        // ────────────────────────────────────────────────────────────────
        //  The plain tier — a letter opens a layer
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void A_plain_letter_opens_a_layer_beyond_the_recorded_debt()
        {
            var arms = LeaderSourceScan.RealSwitchArms();

            // H is exempt by ruling, not by debt: every layer answers H with
            // its own list (#514), so H is the one plain letter that is
            // supposed to be a command.
            var undeclared = UndeclaredOneShots(arms, Keys.None, PlainTierDebt, exempt: new[] { Keys.H });

            Assert.True(undeclared.Count == 0,
                "Plain letters bound to a one-shot command and not recorded as debt: "
                + string.Join(", ", undeclared.Select(Name))
                + ". Under #515 a plain letter OPENS A LAYER. A new toggle goes on the Ctrl "
                + "tier (its own initial), a new action on the Alt tier, and a common toggle "
                + "gets both a layer home and a Ctrl chord — one idea, two doors. If this "
                + "letter genuinely must stay a one-shot for now, record it in PlainTierDebt "
                + "with the reason, so the list Noel walks is complete.");
        }

        [Fact]
        public void The_plain_tier_debt_is_still_owed()
        {
            // Two directions, or the list would drift into a mirror: an entry
            // for a letter that has since become a layer, or been unbound, is
            // paid debt still on the books.
            var stale = StaleDebt(LeaderSourceScan.RealSwitchArms(), Keys.None, PlainTierDebt);

            Assert.True(stale.Count == 0,
                "PlainTierDebt lists letters that are no longer plain one-shots — delete their "
                + "lines, the debt is paid: " + string.Join(", ", stale.Select(Name)));
        }

        [Fact]
        public void The_letters_ruled_for_layers_carry_no_one_shot()
        {
            // A and F are ruled (#514, #512) and their layers are being built.
            // Until a layer's entry lands its letter is unbound on purpose;
            // once it lands the arm is a layer entry. A one-shot on either is
            // the regression the grammar forbids, whatever it does.
            var arms = LeaderSourceScan.RealSwitchArms();
            var offenders = new List<string>();

            foreach (var (letter, layer) in RuledLayerLetters)
            {
                foreach (var arm in arms.Where(a => a.Labels.Contains(letter)))
                {
                    if (!LayerEntry.IsMatch(arm.Body))
                        offenders.Add($"{Name(letter)} is bound to a one-shot but is ruled for {layer}");
                }
            }

            Assert.True(offenders.Count == 0, string.Join("; ", offenders));
        }

        // ────────────────────────────────────────────────────────────────
        //  The Ctrl and Alt tiers — never a layer
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Ctrl_and_Alt_chords_do_not_open_layers_beyond_the_recorded_debt()
        {
            var arms = LeaderSourceScan.RealSwitchArms();

            var layersOnCtrl = arms
                .Where(a => LayerEntry.IsMatch(a.Body))
                .SelectMany(a => a.Labels.Where(l => IsTier(l, Keys.Control)))
                .Select(Name).ToList();
            Assert.True(layersOnCtrl.Count == 0,
                "Ctrl+letter chords that open a layer: " + string.Join(", ", layersOnCtrl)
                + ". Ctrl is the toggle tier (#515); a layer is a plain letter.");

            var layersOnAlt = arms
                .Where(a => LayerEntry.IsMatch(a.Body))
                .SelectMany(a => a.Labels.Where(l => IsTier(l, Keys.Alt)))
                .Where(l => !AltTierLayerDebt.ContainsKey(l))
                .Select(Name).ToList();
            Assert.True(layersOnAlt.Count == 0,
                "Alt+letter chords that open a layer and are not recorded as debt: "
                + string.Join(", ", layersOnAlt)
                + ". Alt is the leftovers tier (#515); a layer is a plain letter.");

            var stale = StaleDebt(arms, Keys.Alt, AltTierLayerDebt, isOneShot: a => LayerEntry.IsMatch(a.Body));
            Assert.True(stale.Count == 0,
                "AltTierLayerDebt lists chords that no longer open a layer — delete their lines: "
                + string.Join(", ", stale.Select(Name)));
        }

        // ────────────────────────────────────────────────────────────────
        //  The two help doors every layer has
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void H_lists_and_the_slash_key_explores_from_the_open_layer_and_the_armed_one()
        {
            // #514 and #519: H is this layer's list; the slash key, with or
            // without Shift, is the explorer. They must be bound in the
            // switch AND offered identically by the help-armed dispatch after
            // an unknown key (#303), or the layer would answer the same key
            // two ways depending on how it was reached.
            var arms = LeaderSourceScan.RealSwitchArms();

            var h = arms.Single(a => a.Labels.Contains(Keys.H));
            Assert.Equal(new[] { Keys.H }, h.Labels);
            Assert.Contains("LeaderKeyHelp(", h.Body);

            var slash = arms.Single(a => a.Labels.Contains(Keys.Oem2));
            Assert.Contains(Keys.Oem2 | Keys.Shift, slash.Labels);
            Assert.Contains("OpenKeyExplorer(", slash.Body);
            Assert.DoesNotContain("LeaderKeyHelp(", slash.Body);

            string src = LeaderSourceScan.ReadSource(Path.Combine("JJFlexWpf", "KeyCommands.cs"));
            int from = src.IndexOf("=== LEADER HELP-ARMED DISPATCH", StringComparison.Ordinal);
            int to = src.IndexOf("Check for leader key trigger", from, StringComparison.Ordinal);
            Assert.True(from >= 0 && to > from, "the help-armed dispatch block was not found in DoCommand");
            string armed = LeaderSourceScan.BlankStringsAndComments(src.Substring(from, to - from));
            Assert.Contains("LeaderKeyHelp()", armed);
            Assert.Contains("OpenKeyExplorer()", armed);
        }

        [Fact]
        public void The_sweep_actually_saw_the_layer()
        {
            // Positive control on the sweep itself: an empty arm list makes
            // every "no offenders" assertion above pass vacuously.
            var arms = LeaderSourceScan.RealSwitchArms();
            Assert.True(arms.Count >= 30, $"only {arms.Count} arms read out of DoLeaderCommand — the scanner is broken");
            Assert.Contains(arms, a => a.Labels.Contains(Keys.V) && LayerEntry.IsMatch(a.Body));   // a known layer
            Assert.Contains(arms, a => a.Labels.Contains(Keys.F | Keys.Shift));                    // slice F itself
        }

        // ────────────────────────────────────────────────────────────────
        //  The checks, as functions over arms, so the planted samples above
        //  exercise exactly the code the real sweep runs
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Every way the slice row can fail its own claim: a letter A to H with
        /// no Shift arm, an arm shared with another chord, or an arm bound to
        /// an index other than the letter's own.
        /// </summary>
        private static List<string> SliceRowFaults(List<LeaderSourceScan.SwitchArm> arms)
        {
            var faults = new List<string>();
            for (int i = 0; i < 8; i++)
            {
                Keys chord = (Keys)('A' + i) | Keys.Shift;
                var arm = arms.FirstOrDefault(a => a.Labels.Contains(chord));
                if (arm == null) { faults.Add(Name(chord)); continue; }

                if (arm.Labels.Count != 1)
                    faults.Add($"{Name(chord)} shares its arm with {string.Join(", ", arm.Labels.Where(l => l != chord).Select(Name))}");

                var jump = SliceJump.Match(arm.Body);
                if (!jump.Success)
                    faults.Add($"{Name(chord)} is bound to something other than a slice jump");
                else if (int.Parse(jump.Groups[1].Value) != i)
                    faults.Add($"{Name(chord)} jumps to slice index {jump.Groups[1].Value}, not {i}");
            }
            return faults;
        }

        /// <summary>
        /// Chords on the given tier whose arm is a one-shot (not a layer entry)
        /// and which the debt list does not declare.
        /// </summary>
        private static List<Keys> UndeclaredOneShots(List<LeaderSourceScan.SwitchArm> arms, Keys tier,
            Dictionary<Keys, string> debt, Keys[] exempt)
        {
            return arms
                .Where(a => !LayerEntry.IsMatch(a.Body))
                .SelectMany(a => a.Labels)
                .Where(l => IsTier(l, tier) && !debt.ContainsKey(l) && !exempt.Contains(l))
                .Distinct()
                .OrderBy(k => k)
                .ToList();
        }

        /// <summary>
        /// Debt entries whose chord no longer has a one-shot arm on that tier
        /// — either it became a layer, moved tiers, or was unbound.
        /// </summary>
        private static List<Keys> StaleDebt(List<LeaderSourceScan.SwitchArm> arms, Keys tier,
            Dictionary<Keys, string> debt, Func<LeaderSourceScan.SwitchArm, bool>? isOneShot = null)
        {
            isOneShot ??= a => !LayerEntry.IsMatch(a.Body);
            return debt.Keys
                .Where(k => IsTier(k, tier))
                .Where(k => !arms.Any(a => a.Labels.Contains(k) && isOneShot(a)))
                .OrderBy(k => k)
                .ToList();
        }

        /// <summary>A letter chord carrying exactly the given modifier set (None for plain).</summary>
        private static bool IsTier(Keys chord, Keys modifiers)
        {
            Keys code = chord & Keys.KeyCode;
            return code >= Keys.A && code <= Keys.Z && (chord & Keys.Modifiers) == modifiers;
        }

        private static string Name(Keys chord)
        {
            string mods = "";
            if ((chord & Keys.Control) != 0) mods += "Ctrl+";
            if ((chord & Keys.Alt) != 0) mods += "Alt+";
            if ((chord & Keys.Shift) != 0) mods += "Shift+";
            return mods + (chord & Keys.KeyCode);
        }
    }
}
