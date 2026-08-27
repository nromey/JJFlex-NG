using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The Audio menu's shape must not depend on whether a radio is connected
    /// (#214).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Noel, 2026-08-24: "when the radio is not connected, alt o then a goes to
    /// audio workshop, then audio devices. When I was connected to the radio
    /// ... a goes directly to audio devices."
    /// </para>
    /// <para>
    /// The mechanism is not simply that items appeared and disappeared. A
    /// Windows menu starts its first-letter search AFTER the highlighted item,
    /// and a popup opens with its first item highlighted — so an item at the
    /// top of a menu is skipped by its own first letter. With no radio, seven
    /// commands were omitted, Audio Devices became the first item, and pressing
    /// A stepped past it. Connected, the same item sat lower and the same press
    /// landed on it. What had to change was not the item count but WHAT WAS
    /// FIRST.
    /// </para>
    /// <para>
    /// So the invariant these tests hold is POSITIONAL: every entry occupies the
    /// same index in both states, and connection state may change nothing but a
    /// suffix on a label. Held against
    /// <see cref="AudioMenuLayout"/>, which is data, so it can be checked
    /// without constructing a window.
    /// </para>
    /// </remarks>
    public class AudioMenuStabilityTests
    {
        private static List<string> LabelsAt(bool connected) =>
            AudioMenuLayout.Entries
                .Select(e => AudioMenuLayout.LabelFor(e, connected))
                .ToList();

        // ────────────────────────────────────────────────────────────────
        //  Prove the instrument before trusting it
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void The_position_check_can_tell_two_different_menus_apart()
        {
            // The positive control. Every assertion below says "these two
            // sequences agree"; that claim is worthless unless the same
            // comparison can report disagreement. Rebuild the OLD behaviour —
            // radio-gated rows omitted when there is no radio — and check that
            // Audio Devices moves, which is exactly what it used to do.
            // The old menu, written out rather than derived, because it is
            // history and the current layout can no longer produce it. Both
            // AddSep calls lived inside the one "if (Rig != null)", so a
            // disconnected Audio menu had no separators either.
            var oldConnected = new[]
            {
                "mute-slice", "mute-all-slices", "release-extra-slices", "pc-audio",
                "pc-audio-levels", "on-radio-levels",
                "audio-devices", "earcon-scratchpad", "audio-workshop",
            };
            var oldDisconnected = new[]
            {
                "audio-devices", "earcon-scratchpad", "audio-workshop",
            };

            Assert.NotEqual(
                Array.IndexOf(oldConnected, "audio-devices"),
                Array.IndexOf(oldDisconnected, "audio-devices"));

            // And it was FIRST when disconnected, which is the position that
            // makes a Windows menu skip a row by its own first letter — the
            // whole reported defect, in one index.
            Assert.Equal(0, Array.IndexOf(oldDisconnected, "audio-devices"));

            // The same ids, so this really is the same menu reordered and not a
            // list that drifted away from what it is being compared against.
            Assert.Equal(
                oldConnected.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                AudioMenuLayout.Entries
                    .Where(e => e.Kind != AudioMenuEntryKind.Separator)
                    .Select(e => e.Id)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToList());
        }

        // ────────────────────────────────────────────────────────────────
        //  The invariant
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Every_entry_sits_at_the_same_index_with_and_without_a_radio()
        {
            // There is one Entries list and both states walk all of it, so this
            // is true by construction — which is the point. It is asserted so
            // that reintroducing an "if connected" filter anywhere in the
            // pipeline fails here rather than in someone's muscle memory.
            var ids = AudioMenuLayout.Entries.Select(e => e.Id).ToList();

            Assert.Equal(ids.Count, LabelsAt(true).Count);
            Assert.Equal(ids.Count, LabelsAt(false).Count);
            Assert.Equal(ids.Distinct().Count(), ids.Count);
        }

        [Fact]
        public void Audio_devices_is_not_the_first_row_in_either_state()
        {
            // The specific defect. An item at index 0 is highlighted when the
            // popup opens and is therefore skipped by its own first letter.
            var rows = AudioMenuLayout.Entries
                .Where(e => e.Kind != AudioMenuEntryKind.Separator)
                .Select(e => e.Id)
                .ToList();

            Assert.True(rows.IndexOf("audio-devices") > 0);
            Assert.True(rows.IndexOf("audio-workshop") > 0);
        }

        [Fact]
        public void The_first_row_that_works_without_a_radio_does_not_start_with_A()
        {
            // The assumption this fix must NOT rest on: that Windows highlights
            // a GREYED first item when a popup opens, rather than skipping to
            // the first enabled one. Nobody can settle that by reading code.
            //
            // If Windows does skip, then with no radio the highlighted row is
            // the first one that works without a radio — and a row is skipped by
            // its own first letter. So that row must not be an A row, or #214
            // returns intact in exactly the state it was reported in.
            var firstUngated = AudioMenuLayout.Entries
                .First(e => e.Kind != AudioMenuEntryKind.Separator && !e.NeedsRadio);

            Assert.NotEqual('A', char.ToUpperInvariant(firstUngated.Label[0]));
        }

        [Fact]
        public void Connection_state_changes_nothing_but_a_suffix_on_gated_rows()
        {
            var connected = LabelsAt(true);
            var disconnected = LabelsAt(false);

            for (int i = 0; i < AudioMenuLayout.Entries.Count; i++)
            {
                var entry = AudioMenuLayout.Entries[i];
                if (!entry.NeedsRadio)
                {
                    Assert.Equal(connected[i], disconnected[i]);
                    continue;
                }

                Assert.NotEqual(connected[i], disconnected[i]);
                Assert.Contains(AudioMenuLayout.NeedsRadioSuffix, disconnected[i]);
                Assert.DoesNotContain(AudioMenuLayout.NeedsRadioSuffix, connected[i]);
            }
        }

        [Fact]
        public void First_letters_are_the_same_sequence_in_both_states()
        {
            // What first-letter navigation actually walks. Windows matches on
            // the first character of the row text, so this sequence IS the
            // gesture; if it differs between states the gesture differs.
            static List<char> Firsts(bool connected) =>
                AudioMenuLayout.Entries
                    .Where(e => e.Kind != AudioMenuEntryKind.Separator)
                    .Select(e => char.ToUpperInvariant(AudioMenuLayout.LabelFor(e, connected)[0]))
                    .ToList();

            Assert.Equal(Firsts(true), Firsts(false));
        }

        [Fact]
        public void The_unavailable_reason_goes_before_the_accelerator_column()
        {
            // Windows lays out everything past a tab as the key column. A
            // reason appended after it would be drawn as part of the keystroke
            // and read as part of it.
            var gatedWithKey = AudioMenuLayout.Entries
                .First(e => e.NeedsRadio && e.Accelerator.Length > 0);

            string label = AudioMenuLayout.LabelFor(gatedWithKey, radioConnected: false);
            int suffixAt = label.IndexOf(AudioMenuLayout.NeedsRadioSuffix, StringComparison.Ordinal);
            int tabAt = label.IndexOf('\t');

            Assert.True(suffixAt >= 0);
            Assert.True(tabAt >= 0);
            Assert.True(suffixAt < tabAt);
        }

        [Fact]
        public void No_row_carries_an_accelerator_or_a_reason_inside_its_own_label()
        {
            // The label is the words only. Anything else in it would be text
            // the builder cannot take back out when the state changes.
            foreach (var entry in AudioMenuLayout.Entries)
            {
                Assert.DoesNotContain("\t", entry.Label);
                Assert.DoesNotContain(AudioMenuLayout.NeedsRadioSuffix, entry.Label);
                if (entry.Kind == AudioMenuEntryKind.Separator)
                    Assert.Equal("", entry.Label);
                else
                    Assert.False(string.IsNullOrWhiteSpace(entry.Label));
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  The layout and the builder cannot drift apart
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Every_layout_row_is_wired_by_the_menu_builder()
        {
            // Reads SOURCE, in the LexiconKeyCoverageTests family, because
            // Radios.Tests cannot load the WPF assembly. A layout row with no
            // handler falls to the builder's default arm and becomes a
            // not-implemented stub — which still holds the position, but is a
            // silent downgrade nobody would notice.
            string src = Source(Path.Combine("JJFlexWpf", "NativeMenuBar.cs"));

            foreach (var entry in AudioMenuLayout.Entries)
            {
                if (entry.Kind == AudioMenuEntryKind.Separator) continue;
                Assert.Contains("case \"" + entry.Id + "\":", src);
            }
        }

        [Fact]
        public void The_menu_builder_no_longer_gates_the_audio_menu_on_a_radio()
        {
            // The regression that would undo all of the above: wrapping part of
            // BuildAudioItems in a connection test again. Look at the method
            // itself rather than the file, so an unrelated Rig check elsewhere
            // does not trip it.
            string method = MethodBody(
                Source(Path.Combine("JJFlexWpf", "NativeMenuBar.cs")),
                "private void BuildAudioItems(IntPtr parent)");

            Assert.DoesNotContain("if (Rig != null)", method);
            Assert.DoesNotContain("if (Rig == null)", method);
            Assert.Contains("AudioMenuLayout.Entries", method);
        }

        [Fact]
        public void The_method_scanner_finds_a_body_and_stops_at_its_end()
        {
            // Positive control for the scanner above: it must return the
            // method's own text and nothing after it, or "does not contain"
            // proves nothing.
            const string sample = @"
    private void Wanted(int x)
    {
        if (Rig == null) { return; }
    }

    private void Other()
    {
        Marker();
    }
";
            string body = MethodBody(sample, "private void Wanted(int x)");
            Assert.Contains("if (Rig == null)", body);
            Assert.DoesNotContain("Marker()", body);
        }

        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The text of one method, from its signature to the brace that closes
        /// it, by brace depth.
        /// </summary>
        private static string MethodBody(string source, string signature)
        {
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, "signature not found: " + signature);

            int open = source.IndexOf('{', at);
            Assert.True(open >= 0, "no body for: " + signature);

            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0) return source.Substring(at, i - at + 1);
                }
            }
            Assert.Fail("unbalanced braces after: " + signature);
            return "";
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
                if (File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            return AppContext.BaseDirectory;
        }
    }
}
