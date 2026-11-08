using System;
using System.IO;
using System.Linq;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The noise surface lives on the top-level Audio menu and NOT on the Slice
    /// menu (#836, and Sprint 49 Track E).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Noel, 2026-10-27 15:59: "This should not be in slice ... this needs to
    /// be moved to audio." It had sat only under Slice since Sprint 23 deleted
    /// the Operations menu that used to hold it, and nobody noticed for seven
    /// months, which is why this is a test and not just a move.
    /// </para>
    /// <para>
    /// <b>Sprint 49 Track E (E8) replaced the DSP submenu</b> with the noise
    /// surface's three rows: "Say what noise reduction is active", then the
    /// two subjects, Noise Reduction and Noise Blankers and Notches (ruled
    /// 2026-11-07, question 2). The tests that named "dsp" now name those
    /// rows; what they hold to is unchanged: top-level only, needing no radio
    /// themselves, built in one place. The first-letter test for D went with
    /// the DSP row: no row begins with D now, and the three new rows' letters
    /// collide by design (#838's defect 3; the JJ key's chords are the fast
    /// path).
    /// </para>
    /// <para>
    /// Two halves, as in <see cref="AudioMenuStabilityTests"/>: the layout is
    /// data and is read directly, and the builder lives in the WPF project,
    /// which Radios.Tests cannot load, so it is read as source.
    /// </para>
    /// </remarks>
    public class DspOnAudioMenuTests
    {
        private static readonly string[] NoiseRows = { "noise-active", "noise-reduction", "noise-blankers" };

        // ────────────────────────────────────────────────────────────────
        //  The layout
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void The_top_level_audio_menu_has_the_noise_rows_and_none_needs_a_radio()
        {
            var top = AudioMenuLayout.EntriesFor(AudioMenuHost.AudioMenu).ToList();
            var active = top.Single(e => e.Id == "noise-active");
            var reduction = top.Single(e => e.Id == "noise-reduction");
            var blankers = top.Single(e => e.Id == "noise-blankers");

            Assert.Equal(AudioMenuEntryKind.Command, active.Kind);
            Assert.Equal("Say what noise reduction is active", active.Label);
            Assert.Equal(AudioMenuEntryKind.Submenu, reduction.Kind);
            Assert.Equal("Noise Reduction", reduction.Label);
            Assert.Equal(AudioMenuEntryKind.Submenu, blankers.Kind);
            Assert.Equal("Noise Blankers and Notches", blankers.Label);

            int at = top.IndexOf(active);
            Assert.Same(reduction, top[at + 1]);
            Assert.Same(blankers, top[at + 2]);

            // Settings are intents: the rows may not come and go with the radio.
            foreach (var row in new[] { active, reduction, blankers })
            {
                Assert.False(row.NeedsRadio);
                Assert.Equal(AudioMenuLayout.LabelFor(row, true), AudioMenuLayout.LabelFor(row, false));
            }
            Assert.DoesNotContain(top, e => e.Id == "dsp");
        }

        [Fact]
        public void The_slice_menus_audio_submenu_has_no_noise_rows()
        {
            var ids = AudioMenuLayout.EntriesFor(AudioMenuHost.SliceAudioSubmenu)
                .Select(e => e.Id).ToList();

            foreach (string id in NoiseRows) Assert.DoesNotContain(id, ids);
            Assert.DoesNotContain("sep-noise", ids);
            Assert.DoesNotContain("meter-tones", ids);
        }

        [Fact]
        public void Meter_tones_have_their_own_row_right_after_the_noise_rows_that_needs_no_radio()
        {
            // Noel, 2026-10-27 20:41: "I think it gets its own row."
            var top = AudioMenuLayout.EntriesFor(AudioMenuHost.AudioMenu).ToList();
            int blankers = top.FindIndex(e => e.Id == "noise-blankers");
            int meters = top.FindIndex(e => e.Id == "meter-tones");

            Assert.True(meters >= 0, "no Meter Tones row on the Audio menu");
            Assert.Equal(blankers + 1, meters);

            var row = top[meters];
            Assert.Equal(AudioMenuEntryKind.Submenu, row.Kind);
            Assert.Equal("Meter Tones", row.Label);
            Assert.False(row.NeedsRadio);
            Assert.Equal(AudioMenuLayout.LabelFor(row, true), AudioMenuLayout.LabelFor(row, false));
        }

        [Fact]
        public void The_two_menus_differ_by_the_noise_and_meter_tone_rows_and_nothing_else()
        {
            var top = AudioMenuLayout.EntriesFor(AudioMenuHost.AudioMenu).Select(e => e.Id).ToList();
            var slice = AudioMenuLayout.EntriesFor(AudioMenuHost.SliceAudioSubmenu).Select(e => e.Id).ToList();
            var topOnly = new[] { "sep-noise", "noise-active", "noise-reduction", "noise-blankers", "meter-tones" };

            Assert.Equal(top.Where(id => !topOnly.Contains(id)).ToList(), slice);

            var flagged = AudioMenuLayout.Entries.Where(e => e.TopLevelOnly).Select(e => e.Id).ToList();
            Assert.Equal(topOnly, flagged);

            Assert.Equal(AudioMenuLayout.Entries.Select(e => e.Id).ToList(), top);
        }

        [Theory]
        [InlineData(AudioMenuHost.AudioMenu)]
        [InlineData(AudioMenuHost.SliceAudioSubmenu)]
        public void Neither_menu_starts_ends_or_doubles_a_separator(AudioMenuHost host)
        {
            // Taking rows out of a list is how a menu ends up with two dividers
            // in a row. The noise rows' separator is flagged with them for this.
            var kinds = AudioMenuLayout.EntriesFor(host).Select(e => e.Kind).ToList();

            Assert.NotEqual(AudioMenuEntryKind.Separator, kinds[0]);
            Assert.NotEqual(AudioMenuEntryKind.Separator, kinds[kinds.Count - 1]);
            for (int i = 1; i < kinds.Count; i++)
                Assert.False(kinds[i] == AudioMenuEntryKind.Separator && kinds[i - 1] == AudioMenuEntryKind.Separator,
                    "two separators in a row at index " + i + " on " + host);
        }

        // ────────────────────────────────────────────────────────────────
        //  The needs-a-radio marker the noise rows share with the Audio menu
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void The_marker_goes_before_the_accelerator_and_only_without_a_radio()
        {
            const string row = "Noise Blanker (NB)\tCtrl+J, N, B";

            Assert.Equal(row, AudioMenuLayout.MarkNeedsRadio(row, radioConnected: true));
            Assert.Equal("Noise Blanker (NB)" + AudioMenuLayout.ConnectRadioSuffix + "\tCtrl+J, N, B",
                AudioMenuLayout.MarkNeedsRadio(row, radioConnected: false));
            Assert.Equal("Legacy NR" + AudioMenuLayout.ConnectRadioSuffix,
                AudioMenuLayout.MarkNeedsRadio("Legacy NR", radioConnected: false));
        }

        [Fact]
        public void The_audio_menus_own_rows_still_read_the_same_through_the_shared_marker()
        {
            var levels = AudioMenuLayout.Entries.Single(e => e.Id == "pc-audio-levels");
            Assert.Equal("PC Audio Levels (this computer) - connect a radio\tCtrl+J, V",
                AudioMenuLayout.LabelFor(levels, radioConnected: false));

            var mute = AudioMenuLayout.Entries.Single(e => e.Id == "mute-slice");
            Assert.Equal("Mute/Unmute Slice - connect a radio", AudioMenuLayout.LabelFor(mute, radioConnected: false));
            Assert.Equal("Mute/Unmute Slice", AudioMenuLayout.LabelFor(mute, radioConnected: true));
        }

        // ────────────────────────────────────────────────────────────────
        //  The builder
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void The_slice_menu_builds_no_noise_submenus()
        {
            string slice = Between(MenuSource(),
                "var slice = AddPopup(bar, sliceLabel);", "// === Band ===");

            Assert.DoesNotContain("BuildNoiseItems", slice);
            Assert.DoesNotContain("BuildDSPItems", slice);
            Assert.Contains("BuildAudioItems(audioSub, Radios.AudioMenuHost.SliceAudioSubmenu)", slice);
        }

        [Fact]
        public void The_top_level_audio_menu_is_built_as_the_audio_menu()
        {
            string audio = Between(MenuSource(),
                "var audio = AddPopup(bar, \"Audi&o\");", "// === Tools ===");

            Assert.Contains("BuildAudioItems(audio, Radios.AudioMenuHost.AudioMenu)", audio);
        }

        [Fact]
        public void The_noise_items_are_built_in_exactly_one_place_each_subject_row()
        {
            string src = MenuSource();

            // One definition plus one call per subject. A third call anywhere
            // (the Slice menu, a context menu) is a second home.
            Assert.Equal(3, Count(src, "BuildNoiseItems("));
            Assert.Contains("BuildNoiseItems(AddSubmenu(parent, label), DenoiseGroup.NoiseReduction)",
                Between(src, "case \"noise-reduction\":", "break;"));
            Assert.Contains("BuildNoiseItems(AddSubmenu(parent, label), DenoiseGroup.BlankersAndNotches)",
                Between(src, "case \"noise-blankers\":", "break;"));
            Assert.DoesNotContain("BuildDSPItems", src);
        }

        [Fact]
        public void The_meter_tone_items_are_built_in_exactly_one_place_their_own_row()
        {
            string src = MenuSource();

            Assert.Equal(2, Count(src, "BuildMeterToneItems("));
            string arm = Between(src, "case \"meter-tones\":", "break;");
            Assert.Contains("BuildMeterToneItems(AddSubmenu(parent, label))", arm);

            // And the noise submenus do not carry them.
            string noise = MethodBody(src, "private void BuildNoiseItems(IntPtr parent, DenoiseGroup group)");
            Assert.DoesNotContain("Meter Tones", noise);
            Assert.DoesNotContain("MeterToneEngine", noise);

            string meters = MethodBody(src, "private void BuildMeterToneItems(IntPtr parent)");
            int at = -1;
            foreach (string row in new[]
                     {
                         "\"Meter Tones On/Off\\tCtrl+J, Ctrl+T\"",
                         "\"Meters Panel\\tCtrl+M\"",
                         "\"Cycle Preset\"",
                         "\"Speak Meters\"",
                         "\"Peak Watcher\"",
                     })
            {
                int next = meters.IndexOf(row, StringComparison.Ordinal);
                Assert.True(next > at, "missing or out of order: " + row);
                at = next;
            }
        }

        [Fact]
        public void The_meter_tone_items_sit_directly_in_the_row_the_layout_made()
        {
            string meters = MethodBody(MenuSource(), "private void BuildMeterToneItems(IntPtr parent)");

            Assert.DoesNotContain("AddSubmenu(", meters);
            Assert.Equal(5, Count(meters, "(parent, "));
        }

        [Fact]
        public void The_meter_rows_that_read_the_radio_say_so_with_no_radio()
        {
            string meters = MethodBody(MenuSource(), "private void BuildMeterToneItems(IntPtr parent)");

            Assert.Contains("Radios.AudioMenuLayout.MarkNeedsRadio(text, radio)", meters);
            Assert.Contains("R(\"Meters Panel\\tCtrl+M\")", meters);
            Assert.Contains("R(\"Speak Meters\")", meters);
            Assert.DoesNotContain("R(\"Meter Tones On/Off", meters);
            Assert.DoesNotContain("R(\"Cycle Preset\")", meters);
            Assert.DoesNotContain("R(\"Peak Watcher\")", meters);
        }

        [Fact]
        public void The_noise_submenus_are_built_with_no_radio_and_act_only_through_the_controller()
        {
            string method = MethodBody(MenuSource(), "private void BuildNoiseItems(IntPtr parent, DenoiseGroup group)");

            // Built in both states: never an early return on a missing radio.
            Assert.DoesNotContain("if (Rig == null) return;", method);
            Assert.Contains("Radios.AudioMenuLayout.MarkNeedsRadio(text, radio)", method);
            // Every engine row comes from the controller's list and acts through it.
            Assert.Contains("noise.Engines(group)", method);
            Assert.Contains("n.Toggle(id, MenuSource)", method);
            Assert.Contains("n.ReturnToDefaults(id, MenuSource)", method);
            Assert.Contains("n.ToggleFavourite(id, MenuSource)", method);
            // Nothing is greyed for availability: the marker is text, from the controller.
            Assert.Contains("NoiseMarked(name, id)", method);
            Assert.DoesNotContain("ToggleDSP(", method);
        }

        [Fact]
        public void The_availability_marker_goes_before_the_accelerator()
        {
            // The marker is composed on the NAME, and the chord text, which
            // carries the tab, is appended after it.
            string method = MethodBody(MenuSource(), "private void BuildNoiseItems(IntPtr parent, DenoiseGroup group)");
            Assert.Contains("R(named + chord)", method);
            string chord = MethodBody(MenuSource(), "private static string NoiseChordText(string id)");
            Assert.Contains("\"\\tCtrl+J, N, \"", chord);
        }

        [Fact]
        public void The_scanners_find_what_they_are_pointed_at()
        {
            // Positive control: every DoesNotContain above is worthless if the
            // section it reads is empty or wrong.
            string slice = Between(MenuSource(),
                "var slice = AddPopup(bar, sliceLabel);", "// === Band ===");
            Assert.Contains("AddSubmenu(slice, \"Receiver\")", slice);
            Assert.Contains("AddSubmenu(slice, \"Transmission\")", slice);

            string method = MethodBody(MenuSource(), "private void BuildNoiseItems(IntPtr parent, DenoiseGroup group)");
            Assert.Contains("BuildNoisePresetItems(", method);
            Assert.DoesNotContain("private void BuildFilterItems", method);

            Assert.Equal(3, Count("a.b.a.b.a", "a"));
        }

        // ────────────────────────────────────────────────────────────────

        private static string MenuSource()
        {
            string path = Path.Combine(RepoRoot(), "JJFlexWpf", "NativeMenuBar.cs");
            Assert.True(File.Exists(path), "source not found: " + path);
            return File.ReadAllText(path);
        }

        private static string Between(string source, string from, string to)
        {
            int start = source.IndexOf(from, StringComparison.Ordinal);
            Assert.True(start >= 0, "marker not found: " + from);
            int end = source.IndexOf(to, start + from.Length, StringComparison.Ordinal);
            Assert.True(end > start, "end marker not found after: " + from);
            return source.Substring(start, end - start);
        }

        private static int Count(string source, string needle)
        {
            int n = 0;
            for (int at = source.IndexOf(needle, StringComparison.Ordinal); at >= 0;
                 at = source.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
                n++;
            return n;
        }

        /// <summary>One method's text, signature to closing brace, by brace depth.</summary>
        private static string MethodBody(string source, string signature)
        {
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, "signature not found: " + signature);

            int open = source.IndexOf('{', at);
            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0) return source.Substring(at, i - at + 1);
            }
            Assert.Fail("unbalanced braces after: " + signature);
            return "";
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
