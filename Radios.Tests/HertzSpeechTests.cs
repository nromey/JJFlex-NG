#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Filter widths are said and shown through ONE formatter, and a 50 Hz
    /// step can never say the same width twice (#747). Measured at the radio:
    /// 1,350 and 1,450 both spoke "1.4 k", and on 2026-11-05 a filter of -400
    /// to 2350 and one of -400 to 2400 both spoke "2.8 k".
    /// </summary>
    /// <remarks>
    /// Noel's ruling, 2026-11-05: the kilo form at Terse, whole hertz at
    /// Chatty. That the kilo form never rounds is Claude's reading of the
    /// ruling, to be confirmed with him; these tests pin that reading.
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class HertzSpeechTests : IDisposable
    {
        private readonly CultureInfo _savedCulture = CultureInfo.CurrentCulture;

        public HertzSpeechTests()
        {
            // The digits follow the operator's culture; pin one so the
            // expected strings below are exact.
            CultureInfo.CurrentCulture = new CultureInfo("en-US");
        }

        public void Dispose() => CultureInfo.CurrentCulture = _savedCulture;

        private static IEnumerable<int> FiftyHertzSteps()
        {
            for (int hz = 1000; hz <= 5000; hz += 50) yield return hz;
        }

        public static IEnumerable<object[]> EveryStyleAndLevel()
        {
            foreach (HertzUnitStyle style in Enum.GetValues(typeof(HertzUnitStyle)))
                foreach (VerbosityLevel level in Enum.GetValues(typeof(VerbosityLevel)))
                    yield return new object[] { style, level };
        }

        [Theory]
        [MemberData(nameof(EveryStyleAndLevel))]
        public void Fifty_hertz_steps_from_1000_to_5000_never_say_the_same_width_twice(HertzUnitStyle style, VerbosityLevel level)
        {
            var said = FiftyHertzSteps().Select(hz => HertzSpeech.Width(hz, style, level)).ToList();
            var repeated = said.GroupBy(s => s).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.True(repeated.Count == 0,
                $"At {level} in the {style} style these widths were said for more than one 50 Hz step: " + string.Join("; ", repeated));
        }

        [Theory]
        [InlineData(HertzUnitStyle.Bare)]
        [InlineData(HertzUnitStyle.KiloLetter)]
        [InlineData(HertzUnitStyle.Symbols)]
        public void Fifty_hertz_steps_never_show_the_same_width_twice(HertzUnitStyle style)
        {
            var shown = FiftyHertzSteps().Select(hz => HertzSpeech.DisplayedWidth(hz, style)).ToList();
            Assert.Equal(shown.Count, shown.Distinct().Count());
        }

        [Fact]
        public void The_kilo_form_never_rounds()
        {
            for (int hz = 0; hz <= 12000; hz += 5)
            {
                string digits = HertzSpeech.KiloDigits(hz);
                decimal back = decimal.Parse(digits, CultureInfo.CurrentCulture) * 1000m;
                Assert.True(back == hz, $"{hz} Hz became \"{digits}\" k, which is {back} Hz.");
            }
        }

        /// <summary>
        /// The width sentence's slot at Terse and at Chatty for the three
        /// widths the report quotes, and the field's two collisions.
        /// </summary>
        [Theory]
        [InlineData(2750, "2.75 k", "2750")]
        [InlineData(2800, "2.8 k", "2800")]
        [InlineData(2850, "2.85 k", "2850")]
        [InlineData(1350, "1.35 k", "1350")]
        [InlineData(1450, "1.45 k", "1450")]
        [InlineData(2000, "2 k", "2000")]
        public void Terse_speaks_the_kilo_form_and_chatty_speaks_whole_hertz(int hz, string terse, string chatty)
        {
            // #979: the units switch adds or removes only "hertz"; the kilo
            // form keeps its "k" either way. This pinned the off form as the
            // only one until the switch, which starts on.
            Assert.Equal(terse, HertzSpeech.Width(hz, HertzUnitStyle.Bare, VerbosityLevel.Terse, units: true));
            Assert.Equal(terse, HertzSpeech.Width(hz, HertzUnitStyle.Bare, VerbosityLevel.Terse, units: false));
            Assert.Equal(chatty + " hertz", HertzSpeech.Width(hz, HertzUnitStyle.Bare, VerbosityLevel.Chatty, units: true));
            Assert.Equal(chatty, HertzSpeech.Width(hz, HertzUnitStyle.Bare, VerbosityLevel.Chatty, units: false));
        }

        // ------------------------------------------------------------------
        // The J13 and J15 rulings (Noel, 2026-11-06 11:38 and 16:01): a filter
        // READOUT says "Filter 100 to 2900, width 2800 hertz" at Chatty (J15
        // replaced J13's "2800 hertz wide" on every one), and keeps the
        // never-rounding kilo form at Terse. Every expected string below is a
        // hand-written literal, assembled as the operator hears it, and DRAFT.
        // ------------------------------------------------------------------

        [Theory]
        [MemberData(nameof(EveryStyleAndLevel))]
        public void A_readouts_width_never_says_the_same_width_twice(HertzUnitStyle style, VerbosityLevel level)
        {
            var said = FiftyHertzSteps().Select(hz => HertzSpeech.ReadoutWidth(hz, style, level)).ToList();
            Assert.Equal(said.Count, said.Distinct().Count());
        }

        [Theory]
        [InlineData(2750, "2.75 k", "width 2750 hertz")]
        [InlineData(2800, "2.8 k", "width 2800 hertz")]
        [InlineData(2850, "2.85 k", "width 2850 hertz")]
        [InlineData(500, "500 hertz", "width 500 hertz")]   // #979: units on by default; J30's bare "500" is the off form
        public void The_bracket_readouts_width_is_short_at_terse_and_labelled_at_chatty(int hz, string terse, string chatty)
        {
            Assert.Equal(terse, HertzSpeech.ReadoutWidth(hz, HertzUnitStyle.Bare, VerbosityLevel.Terse));
            Assert.Equal(terse, HertzSpeech.ReadoutWidth(hz, HertzUnitStyle.Bare, VerbosityLevel.Critical));
            Assert.Equal(chatty, HertzSpeech.ReadoutWidth(hz, HertzUnitStyle.Bare, VerbosityLevel.Chatty));
            Assert.Equal(chatty, HertzSpeech.ReadoutWidth(hz, HertzUnitStyle.Bare, VerbosityLevel.Diagnostic));
        }

        [Theory]
        [InlineData(100, 2850, VerbosityLevel.Terse, "Filter 100 to 2850, 2.75 k")]
        [InlineData(100, 2900, VerbosityLevel.Terse, "Filter 100 to 2900, 2.8 k")]
        [InlineData(100, 2950, VerbosityLevel.Terse, "Filter 100 to 2950, 2.85 k")]
        [InlineData(100, 2850, VerbosityLevel.Chatty, "Filter 100 to 2850, width 2750 hertz")]
        [InlineData(100, 2900, VerbosityLevel.Chatty, "Filter 100 to 2900, width 2800 hertz")]
        [InlineData(100, 2950, VerbosityLevel.Chatty, "Filter 100 to 2950, width 2850 hertz")]
        [InlineData(100, 600, VerbosityLevel.Terse, "Filter 100 to 600, 500 hertz")]   // #979: units on by default
        [InlineData(100, 600, VerbosityLevel.Chatty, "Filter 100 to 600, width 500 hertz")]
        public void The_bracket_readout_assembled(int low, int high, VerbosityLevel level, string heard)
        {
            Assert.Equal(heard, Lexicon.Get("settings.filter.range", ("low", low), ("high", high),
                ("width", HertzSpeech.ReadoutWidth(high - low, HertzUnitStyle.Bare, level)), ("limit", "")));
        }

        [Theory]
        [InlineData(VerbosityLevel.Terse, "RX filter 100 to 2850, 2.75 k", "Width 2.75 k, 100 to 2850", "TX filter 100 to 2850, 2.75 k", "TX width 2.75 k, 100 to 2850")]
        [InlineData(VerbosityLevel.Chatty, "RX filter 100 to 2850, width 2750 hertz", "RX filter 100 to 2850, width 2750 hertz", "TX filter 100 to 2850, width 2750 hertz", "TX filter 100 to 2850, width 2750 hertz")]
        [InlineData(VerbosityLevel.Critical, "RX filter 100 to 2850, 2.75 k", "Width 2.75 k, 100 to 2850", "TX filter 100 to 2850, 2.75 k", "TX width 2.75 k, 100 to 2850")]
        [InlineData(VerbosityLevel.Diagnostic, "RX filter 100 to 2850, width 2750 hertz", "RX filter 100 to 2850, width 2750 hertz", "TX filter 100 to 2850, width 2750 hertz", "TX filter 100 to 2850, width 2750 hertz")]
        public void The_filter_layers_readouts_assembled(VerbosityLevel level, string rxReport, string rxWidth, string txReport, string txWidth)
        {
            string W(int hz) => HertzSpeech.ReadoutWidth(hz, HertzUnitStyle.KiloLetter, level);
            Assert.Equal(rxReport, Lexicon.Get("audio.filter.rx_report", ("low", 100), ("high", 2850), ("width", W(2750))));
            Assert.Equal(rxWidth, Lexicon.Get("audio.filter_layer.width", level, ("width", W(2750)), ("low", 100), ("high", 2850)));
            Assert.Equal(txReport, Lexicon.Get("audio.filter.tx_report", ("low", 100), ("high", 2850), ("width", W(2750))));
            Assert.Equal(txWidth, Lexicon.Get("audio.filter_layer.tx_width", level, ("width", W(2750)), ("low", 100), ("high", 2850)));
            // Below 1 kHz at Terse: "hertz" while the units switch is on, its
            // default (#979); J30's bare number is the off form.
            if (level == VerbosityLevel.Terse)
                Assert.Equal("Width 500 hertz, 100 to 600", Lexicon.Get("audio.filter_layer.width", level, ("width", W(500)), ("low", 100), ("high", 600)));
            if (level == VerbosityLevel.Terse)
                Assert.Equal("RX filter 100 to 600, 500 hertz", Lexicon.Get("audio.filter.rx_report", ("low", 100), ("high", 600), ("width", W(500))));
            if (level == VerbosityLevel.Terse)
                Assert.Equal("RX filter 100 to 600, 500", Lexicon.Get("audio.filter.rx_report", ("low", 100), ("high", 600),
                    ("width", HertzSpeech.ReadoutWidth(500, HertzUnitStyle.KiloLetter, level, units: false))));
            if (level == VerbosityLevel.Chatty)
                Assert.Equal("RX filter 100 to 600, width 500 hertz", Lexicon.Get("audio.filter.rx_report", ("low", 100), ("high", 600), ("width", W(500))));
        }

        /// <summary>
        /// J30 (Noel, 2026-11-06 17:34, "agreed"): every Terse readout under
        /// 1 kHz is the bare number, whatever the route's style; at and above
        /// 1 kHz Terse keeps "2.8 k", and Chatty is unchanged.
        /// </summary>
        [Theory]
        [InlineData(HertzUnitStyle.Bare)]
        [InlineData(HertzUnitStyle.KiloLetter)]
        [InlineData(HertzUnitStyle.Symbols)]
        public void At_terse_a_readout_under_1_kilohertz_is_the_bare_number(HertzUnitStyle style)
        {
            // #979: the bare number is the units switch's off form now; on,
            // its default, adds "hertz".
            Assert.Equal("500", HertzSpeech.ReadoutWidth(500, style, VerbosityLevel.Terse, units: false));
            Assert.Equal("250", HertzSpeech.ReadoutWidth(250, style, VerbosityLevel.Critical, units: false));
            Assert.Equal("500 hertz", HertzSpeech.ReadoutWidth(500, style, VerbosityLevel.Terse, units: true));
            Assert.Equal("2.8 k", HertzSpeech.ReadoutWidth(2800, style == HertzUnitStyle.Symbols ? HertzUnitStyle.KiloLetter : style, VerbosityLevel.Terse));
            Assert.Equal("width 500 hertz", HertzSpeech.ReadoutWidth(500, style, VerbosityLevel.Chatty));
        }

        [Fact]
        public void Critical_speaks_as_terse_and_diagnostic_as_chatty()
        {
            foreach (int hz in FiftyHertzSteps())
            {
                foreach (HertzUnitStyle style in Enum.GetValues(typeof(HertzUnitStyle)))
                {
                    Assert.Equal(HertzSpeech.Width(hz, style, VerbosityLevel.Terse), HertzSpeech.Width(hz, style, VerbosityLevel.Critical));
                    Assert.Equal(HertzSpeech.Width(hz, style, VerbosityLevel.Chatty), HertzSpeech.Width(hz, style, VerbosityLevel.Diagnostic));
                }
            }
        }

        [Theory]
        [InlineData(VerbosityLevel.Critical)]
        [InlineData(VerbosityLevel.Terse)]
        [InlineData(VerbosityLevel.Chatty)]
        [InlineData(VerbosityLevel.Diagnostic)]
        public void Below_one_kilohertz_a_width_is_its_plain_number_at_every_level(VerbosityLevel level)
        {
            // #979: the plain number with units off; on, its default, adds "hertz".
            Assert.Equal("500", HertzSpeech.Width(500, HertzUnitStyle.Bare, level, units: false));
            Assert.Equal("950", HertzSpeech.Width(950, HertzUnitStyle.Bare, level, units: false));
            Assert.Equal("500 hertz", HertzSpeech.Width(500, HertzUnitStyle.Bare, level, units: true));
        }

        // ------------------------------------------------------------------
        // #979: ONE switch for the word "hertz" on spoken filter widths, at
        // every level, starting on (Noel, 2026-11-11 11:57 and 11:58; the
        // orchestrator's 12:20 clarification: it adds or removes only the
        // word, and each level keeps its own number form).
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(500, VerbosityLevel.Terse, "500 hertz", "500")]
        [InlineData(500, VerbosityLevel.Critical, "500 hertz", "500")]
        [InlineData(2750, VerbosityLevel.Terse, "2.75 k", "2.75 k")]
        [InlineData(2750, VerbosityLevel.Critical, "2.75 k", "2.75 k")]
        [InlineData(2750, VerbosityLevel.Chatty, "width 2750 hertz", "width 2750")]
        [InlineData(500, VerbosityLevel.Chatty, "width 500 hertz", "width 500")]
        [InlineData(2750, VerbosityLevel.Diagnostic, "width 2750 hertz", "width 2750")]
        public void The_units_switch_adds_or_removes_only_the_word_hertz(int hz, VerbosityLevel level, string on, string off)
        {
            foreach (HertzUnitStyle style in new[] { HertzUnitStyle.Bare, HertzUnitStyle.KiloLetter })
            {
                Assert.Equal(on, HertzSpeech.ReadoutWidth(hz, style, level, units: true));
                Assert.Equal(off, HertzSpeech.ReadoutWidth(hz, style, level, units: false));
            }
        }

        [Fact]
        public void The_switch_starts_on_and_the_readouts_follow_it()
        {
            Assert.True(new PttConfig().FilterWidthUnits, "the switch must start on (ruled 2026-11-11 11:58)");
            bool before = HertzSpeech.SpeakUnits;
            try
            {
                HertzSpeech.SpeakUnits = true;
                Assert.Equal("500 hertz", HertzSpeech.ReadoutWidth(500, HertzUnitStyle.Bare, VerbosityLevel.Terse));
                Assert.Equal("width 2750 hertz", HertzSpeech.ReadoutWidth(2750, HertzUnitStyle.KiloLetter, VerbosityLevel.Chatty));
                Assert.Equal("2750 hertz", HertzSpeech.WholeHertz(2750));
                HertzSpeech.SpeakUnits = false;
                Assert.Equal("500", HertzSpeech.ReadoutWidth(500, HertzUnitStyle.Bare, VerbosityLevel.Terse));
                Assert.Equal("width 2750", HertzSpeech.ReadoutWidth(2750, HertzUnitStyle.KiloLetter, VerbosityLevel.Chatty));
                Assert.Equal("2.75 k", HertzSpeech.ReadoutWidth(2750, HertzUnitStyle.KiloLetter, VerbosityLevel.Terse));
                Assert.Equal("2750", HertzSpeech.WholeHertz(2750));
                Assert.Equal("500", HertzSpeech.Width(500, HertzUnitStyle.Bare, VerbosityLevel.Terse));
            }
            finally { HertzSpeech.SpeakUnits = before; }
        }

        [Fact]
        public void Displayed_widths_are_not_on_the_units_switch()
        {
            bool before = HertzSpeech.SpeakUnits;
            try
            {
                HertzSpeech.SpeakUnits = false;
                string off = HertzSpeech.DisplayedWidth(500, HertzUnitStyle.Symbols);
                HertzSpeech.SpeakUnits = true;
                Assert.Equal(off, HertzSpeech.DisplayedWidth(500, HertzUnitStyle.Symbols));
                Assert.Equal("500 Hz", off);
            }
            finally { HertzSpeech.SpeakUnits = before; }
        }

        [Fact]
        public void The_switch_is_saved_with_the_operators_settings_and_made_live_at_connect_and_after_Settings()
        {
            string dir = Path.Combine(Path.GetTempPath(), "jjflex-979-" + Guid.NewGuid().ToString("N"));
            try
            {
                new PttConfig { FilterWidthUnits = false }.Save(dir, "op");
                Assert.False(PttConfig.Load(dir, "op").FilterWidthUnits);
                new PttConfig { FilterWidthUnits = true }.Save(dir, "op");
                Assert.True(PttConfig.Load(dir, "op").FilterWidthUnits);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }

            string dialog = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "Dialogs", "SettingsDialog.xaml.cs"));
            Assert.Contains("FilterUnitsCheckbox.IsChecked = _pttConfig.FilterWidthUnits;", dialog);
            Assert.Contains("_pttConfig.FilterWidthUnits = FilterUnitsCheckbox.IsChecked != false;", dialog);
            string xaml = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "Dialogs", "SettingsDialog.xaml"));
            int tuning = xaml.IndexOf("Header=\"Tuning\"", StringComparison.Ordinal);
            int license = xaml.IndexOf("Header=\"License\"", StringComparison.Ordinal);
            int box = xaml.IndexOf("x:Name=\"FilterUnitsCheckbox\"", StringComparison.Ordinal);
            Assert.True(tuning >= 0 && box > tuning && box < license, "the units switch is not on the Tuning tab");
            string main = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "MainWindow.xaml.cs"));
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(main,
                @"Radios\.HertzSpeech\.SpeakUnits = CurrentPttConfig\.FilterWidthUnits;").Count);
        }

        [Fact]
        public void The_transmit_filter_edge_chords_speak_their_width_through_the_switch()
        {
            // The two chords that said "width {raw}" outside the formatter (#979).
            string keys = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "KeyCommands.cs"));
            Assert.Contains("\"audio.tx_filter.low_moved\", (\"newLow\", l), (\"width\", Radios.HertzSpeech.WholeHertz(h - l))", keys);
            Assert.Contains("\"audio.tx_filter.high_moved\", (\"newHigh\", h), (\"width\", Radios.HertzSpeech.WholeHertz(h - l))", keys);
            Assert.DoesNotContain("(\"width\", h - l)", keys);
        }

        [Fact]
        public void Displayed_text_is_not_on_the_verbosity_dial()
        {
            Assert.Equal(
                HertzSpeech.Width(2750, HertzUnitStyle.Symbols, VerbosityLevel.Terse),
                HertzSpeech.DisplayedWidth(2750, HertzUnitStyle.Symbols));
            Assert.NotEqual(
                HertzSpeech.Width(2750, HertzUnitStyle.Symbols, VerbosityLevel.Chatty),
                HertzSpeech.DisplayedWidth(2750, HertzUnitStyle.Symbols));
        }

        // ------------------------------------------------------------------
        // The class, not the instance: no new hand-rolled kilo width format
        // ------------------------------------------------------------------

        /// <summary>
        /// A width divided by a thousand and given a number format, anywhere
        /// but the shared formatter. Each of the seven sites #747 moved had
        /// this shape; the scan refuses an eighth.
        /// </summary>
        private static readonly Regex HandRolledKiloWidth = new Regex(
            @"/\s*1000(\.0)?[dDmMRr]?\s*\)?\s*(\.ToString\(\s*""[FfNn0#][^""]*""|:\s*(0\.#|[FfNn]\d)|,\s*""[FfNn0#][^""]*"")",
            RegexOptions.Compiled);

        internal static bool IsHandRolledKiloWidth(string line)
            => line.IndexOf("width", StringComparison.OrdinalIgnoreCase) >= 0
               && HandRolledKiloWidth.IsMatch(line);

        [Fact]
        public void The_scan_recognises_each_shape_it_claims_to_refuse()
        {
            // The positive control: every form the seven sites used before #747.
            Assert.True(IsHandRolledKiloWidth("? Words(\"width_kilo\", (\"width\", (width / 1000.0).ToString(\"F1\")))"));
            Assert.True(IsHandRolledKiloWidth("string widthStr = width >= 1000 ? $\"{width / 1000.0:0.#}k\" : $\"{width} hertz\";"));
            Assert.True(IsHandRolledKiloWidth("(\"widthKHz\", ((high - low) / 1000.0).ToString(\"F1\")));"));
            Assert.True(IsHandRolledKiloWidth("(\"filterWidth\", $\"{filterWidth / 1000.0:F1}\"))"));
            Assert.True(IsHandRolledKiloWidth("? Words(\"width_khz\", (\"width\", $\"{p.Width / 1000.0:0.#}\"))"));
            // VB's Format(..., "0.0"), the other source language in the tree.
            Assert.True(IsHandRolledKiloWidth("Dim w As String = Format(width / 1000, \"0.0\")"));
            // And what it must leave alone: a sample rate, a duration.
            Assert.False(IsHandRolledKiloWidth("return $\"{quality} — {rate / 1000.0:0.#} kHz\""));
            Assert.False(IsHandRolledKiloWidth("string seconds = (durationMs / 1000.0).ToString(\"F1\");"));
        }

        [Fact]
        public void No_filter_width_is_formatted_in_kilohertz_outside_the_shared_formatter()
        {
            // The TRACKED tree (git ls-files, plus untracked source not yet
            // ignored), in every source language: C# and VB. It read whatever
            // was on the disk under two folders, C# only, until the
            // integration pass (finding 10), the shape DelegateSurfaceTests
            // was rebuilt to escape.
            var offenders = new List<string>();
            int scanned = 0;
            foreach (string file in IntegrationPassTree.AuthoredSource.Concat(IntegrationPassTree.UntrackedSource()))
            {
                if (IntegrationPassTree.IsTest(file)) continue;
                string rel = IntegrationPassTree.Relative(file).Replace('\\', '/');
                if (rel == "Radios/HertzSpeech.cs") continue;
                scanned++;
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (IsHandRolledKiloWidth(lines[i]))
                        offenders.Add($"{rel}:{i + 1}: {lines[i].Trim()}");
                }
            }
            Assert.True(scanned > 300, "The scan saw only " + scanned + " files, so it was not looking at the source tree.");
            Assert.True(offenders.Count == 0,
                "A filter width is formatted in kilohertz by hand. Use Radios.HertzSpeech.Width (spoken) or " +
                "HertzSpeech.DisplayedWidth (shown), so a 50 Hz step cannot say the same width twice (#747): " +
                string.Join("; ", offenders));
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln")))
                dir = dir.Parent;
            Assert.True(dir != null, "Could not find the repository root above " + AppContext.BaseDirectory);
            return dir!.FullName;
        }
    }
}
