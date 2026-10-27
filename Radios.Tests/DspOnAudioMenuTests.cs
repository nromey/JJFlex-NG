using System;
using System.IO;
using System.Linq;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The DSP submenu lives on the top-level Audio menu and NOT on the Slice
    /// menu (#836).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Noel, 2026-10-27 15:59: "This should not be in slice ... this needs to
    /// be moved to audio." And 16:29: "it needs to move to audio first,
    /// changing on the active slice, then when we do 586, we add per slice
    /// changes." It had sat only under Slice since Sprint 23 deleted the
    /// Operations menu that used to hold it, and nobody noticed for seven
    /// months, which is why this is a test and not just a move.
    /// </para>
    /// <para>
    /// Two halves, as in <see cref="AudioMenuStabilityTests"/>: the layout is
    /// data and is read directly, and the builder lives in the WPF project,
    /// which Radios.Tests cannot load, so it is read as source.
    /// </para>
    /// </remarks>
    public class DspOnAudioMenuTests
    {
        // ────────────────────────────────────────────────────────────────
        //  The layout
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void The_top_level_audio_menu_has_a_DSP_submenu_that_needs_no_radio()
        {
            var dsp = AudioMenuLayout.EntriesFor(AudioMenuHost.AudioMenu)
                .SingleOrDefault(e => e.Id == "dsp");

            Assert.NotNull(dsp);
            Assert.Equal(AudioMenuEntryKind.Submenu, dsp!.Kind);
            Assert.Equal("DSP", dsp.Label);

            // Settings are intents: the row may not come and go with the radio.
            // What inside it needs a radio says so row by row.
            Assert.False(dsp.NeedsRadio);
            Assert.Equal(AudioMenuLayout.LabelFor(dsp, true), AudioMenuLayout.LabelFor(dsp, false));
        }

        [Fact]
        public void The_slice_menus_audio_submenu_has_no_DSP_row()
        {
            var ids = AudioMenuLayout.EntriesFor(AudioMenuHost.SliceAudioSubmenu)
                .Select(e => e.Id).ToList();

            Assert.DoesNotContain("dsp", ids);
            Assert.DoesNotContain("sep-dsp", ids);
        }

        [Fact]
        public void The_two_menus_differ_by_the_DSP_rows_and_nothing_else()
        {
            // Parity between the top-level Audio menu and Slice, Audio stays
            // structural: the slice copy is the top-level list with the flagged
            // rows taken out, in the same order. And the flag is on the DSP
            // rows only, so nobody quietly takes another row off the Slice menu.
            var top = AudioMenuLayout.EntriesFor(AudioMenuHost.AudioMenu).Select(e => e.Id).ToList();
            var slice = AudioMenuLayout.EntriesFor(AudioMenuHost.SliceAudioSubmenu).Select(e => e.Id).ToList();

            Assert.Equal(top.Where(id => id != "dsp" && id != "sep-dsp").ToList(), slice);

            var flagged = AudioMenuLayout.Entries.Where(e => e.TopLevelOnly).Select(e => e.Id).ToList();
            Assert.Equal(new[] { "sep-dsp", "dsp" }, flagged);

            // The top-level menu is the whole layout, so the #214 tests that
            // walk Entries are walking the menu with the DSP row in it.
            Assert.Equal(AudioMenuLayout.Entries.Select(e => e.Id).ToList(), top);
        }

        [Theory]
        [InlineData(AudioMenuHost.AudioMenu)]
        [InlineData(AudioMenuHost.SliceAudioSubmenu)]
        public void Neither_menu_starts_ends_or_doubles_a_separator(AudioMenuHost host)
        {
            // Taking rows out of a list is how a menu ends up with two dividers
            // in a row. The DSP separator is flagged with its row for this.
            var kinds = AudioMenuLayout.EntriesFor(host).Select(e => e.Kind).ToList();

            Assert.NotEqual(AudioMenuEntryKind.Separator, kinds[0]);
            Assert.NotEqual(AudioMenuEntryKind.Separator, kinds[kinds.Count - 1]);
            for (int i = 1; i < kinds.Count; i++)
                Assert.False(kinds[i] == AudioMenuEntryKind.Separator && kinds[i - 1] == AudioMenuEntryKind.Separator,
                    "two separators in a row at index " + i + " on " + host);
        }

        [Fact]
        public void D_reaches_the_DSP_row_in_one_press_on_the_audio_menu()
        {
            // No other row on the Audio menu begins with D or underlines it, so
            // first-letter navigation lands on DSP without cycling.
            var rows = AudioMenuLayout.EntriesFor(AudioMenuHost.AudioMenu)
                .Where(e => e.Kind != AudioMenuEntryKind.Separator && e.Id != "dsp")
                .ToList();

            foreach (var row in rows)
            {
                Assert.NotEqual('D', char.ToUpperInvariant(row.Label[0]));
                int amp = row.Label.IndexOf('&');
                if (amp >= 0 && amp + 1 < row.Label.Length)
                    Assert.NotEqual('D', char.ToUpperInvariant(row.Label[amp + 1]));
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  The needs-a-radio marker the DSP rows share with the Audio menu
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void The_marker_goes_before_the_accelerator_and_only_without_a_radio()
        {
            const string row = "Noise Blanker (NB)\tCtrl+J, N, B";

            Assert.Equal(row, AudioMenuLayout.MarkNeedsRadio(row, radioConnected: true));
            Assert.Equal("Noise Blanker (NB)" + AudioMenuLayout.NeedsRadioSuffix + "\tCtrl+J, N, B",
                AudioMenuLayout.MarkNeedsRadio(row, radioConnected: false));
            Assert.Equal("Legacy NR" + AudioMenuLayout.NeedsRadioSuffix,
                AudioMenuLayout.MarkNeedsRadio("Legacy NR", radioConnected: false));
        }

        [Fact]
        public void The_audio_menus_own_rows_still_read_the_same_through_the_shared_marker()
        {
            // LabelFor now routes through MarkNeedsRadio. Pin the two shapes it
            // produced before, so the refactor cannot have moved a reason.
            var levels = AudioMenuLayout.Entries.Single(e => e.Id == "pc-audio-levels");
            Assert.Equal("PC Audio Levels (this computer) - needs a radio\tCtrl+J, V",
                AudioMenuLayout.LabelFor(levels, radioConnected: false));

            var mute = AudioMenuLayout.Entries.Single(e => e.Id == "mute-slice");
            Assert.Equal("Mute/Unmute Slice - needs a radio", AudioMenuLayout.LabelFor(mute, radioConnected: false));
            Assert.Equal("Mute/Unmute Slice", AudioMenuLayout.LabelFor(mute, radioConnected: true));
        }

        // ────────────────────────────────────────────────────────────────
        //  The builder
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void The_slice_menu_builds_no_DSP_submenu()
        {
            // The Slice popup's whole build, from the popup to the next one on
            // the bar. The removed lines were AddSubmenu(slice, "DSP") and a
            // BuildDSPItems call; either coming back is the defect.
            string slice = Between(MenuSource(),
                "var slice = AddPopup(bar, sliceLabel);", "// === Band ===");

            Assert.DoesNotContain("BuildDSPItems", slice);
            Assert.DoesNotContain("\"DSP\"", slice);
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
        public void The_DSP_items_are_built_in_exactly_one_place_the_audio_menus_dsp_row()
        {
            string src = MenuSource();

            // One definition plus one call. A second call anywhere - the Slice
            // menu, a context menu, a copy for Classic - is a second home.
            Assert.Equal(2, Count(src, "BuildDSPItems("));

            string arm = Between(src, "case \"dsp\":", "break;");
            Assert.Contains("BuildDSPItems(AddSubmenu(parent, label))", arm);
        }

        [Fact]
        public void The_DSP_submenu_is_built_with_no_radio_and_marks_what_needs_one()
        {
            string method = MethodBody(MenuSource(), "private void BuildDSPItems(IntPtr parent)");

            // It used to return empty with no radio. On the Audio menu that is an
            // empty submenu, and a shape that changes with the connection (#214).
            Assert.DoesNotContain("if (Rig == null) return;", method);
            Assert.Contains("Radios.AudioMenuLayout.MarkNeedsRadio(text, radio)", method);

            // Every row that can do nothing without the radio is marked. The
            // PC-side rows and meter tones are deliberately not on this list.
            foreach (string label in new[]
                     {
                         "R(NrItemLabel(\"On-Radio Neural NR (RNN)",
                         "R(NrItemLabel(\"On-Radio Spectral NR (NRS)",
                         "R(NrItemLabel(\"NR Filter (NRF)",
                         "R(\"Legacy NR\")",
                         "R(\"NR Level Up\")",
                         "R(\"NR Level Down\")",
                         "R(\"Noise Blanker (NB)",
                         "R(\"NB Level Up\")",
                         "R(\"NB Level Down\")",
                         "R(\"Wideband NB (WNB)",
                         "R(\"WNB Level Up\")",
                         "R(\"WNB Level Down\")",
                         "R(\"Capture Noise Profile",
                         "R(\"FFT Auto-Notch",
                         "R(\"Legacy Auto-Notch\")",
                         "R(\"Audio Peak Filter (APF)",
                     })
            {
                Assert.Contains(label, method);
            }
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

            string method = MethodBody(MenuSource(), "private void BuildDSPItems(IntPtr parent)");
            Assert.Contains("ToggleDSP(\"Legacy NR\"", method);
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
