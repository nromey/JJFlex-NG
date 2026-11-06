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
            Assert.Equal(terse, HertzSpeech.Width(hz, HertzUnitStyle.Bare, VerbosityLevel.Terse));
            Assert.Equal(chatty, HertzSpeech.Width(hz, HertzUnitStyle.Bare, VerbosityLevel.Chatty));
        }

        // ------------------------------------------------------------------
        // The J13 ruling (Noel, 2026-11-06 11:38 and 11:39): a filter READOUT
        // says "Filter 100 to 2900, 2800 hertz wide" at Chatty, and keeps the
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
        [InlineData(2750, "2.75 k", "2750 hertz wide")]
        [InlineData(2800, "2.8 k", "2800 hertz wide")]
        [InlineData(2850, "2.85 k", "2850 hertz wide")]
        [InlineData(500, "500", "500 hertz wide")]
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
        [InlineData(100, 2850, VerbosityLevel.Chatty, "Filter 100 to 2850, 2750 hertz wide")]
        [InlineData(100, 2900, VerbosityLevel.Chatty, "Filter 100 to 2900, 2800 hertz wide")]
        [InlineData(100, 2950, VerbosityLevel.Chatty, "Filter 100 to 2950, 2850 hertz wide")]
        [InlineData(100, 600, VerbosityLevel.Terse, "Filter 100 to 600, 500")]
        [InlineData(100, 600, VerbosityLevel.Chatty, "Filter 100 to 600, 500 hertz wide")]
        public void The_bracket_readout_assembled(int low, int high, VerbosityLevel level, string heard)
        {
            Assert.Equal(heard, Lexicon.Get("settings.filter.range", ("low", low), ("high", high),
                ("width", HertzSpeech.ReadoutWidth(high - low, HertzUnitStyle.Bare, level)), ("limit", "")));
        }

        [Theory]
        [InlineData(VerbosityLevel.Terse, "RX filter 100 to 2850, 2.75 k", "Width 2.75 k, 100 to 2850", "TX filter 100 to 2850, 2.75 k", "TX width 2.75 k, 100 to 2850")]
        [InlineData(VerbosityLevel.Chatty, "RX filter 100 to 2850, 2750 hertz wide", "RX filter 100 to 2850, 2750 hertz wide", "TX filter 100 to 2850, 2750 hertz wide", "TX filter 100 to 2850, 2750 hertz wide")]
        [InlineData(VerbosityLevel.Critical, "RX filter 100 to 2850, 2.75 k", "Width 2.75 k, 100 to 2850", "TX filter 100 to 2850, 2.75 k", "TX width 2.75 k, 100 to 2850")]
        [InlineData(VerbosityLevel.Diagnostic, "RX filter 100 to 2850, 2750 hertz wide", "RX filter 100 to 2850, 2750 hertz wide", "TX filter 100 to 2850, 2750 hertz wide", "TX filter 100 to 2850, 2750 hertz wide")]
        public void The_filter_layers_readouts_assembled(VerbosityLevel level, string rxReport, string rxWidth, string txReport, string txWidth)
        {
            string W(int hz) => HertzSpeech.ReadoutWidth(hz, HertzUnitStyle.KiloLetter, level);
            Assert.Equal(rxReport, Lexicon.Get("audio.filter.rx_report", ("low", 100), ("high", 2850), ("width", W(2750))));
            Assert.Equal(rxWidth, Lexicon.Get("audio.filter_layer.width", level, ("width", W(2750)), ("low", 100), ("high", 2850)));
            Assert.Equal(txReport, Lexicon.Get("audio.filter.tx_report", ("low", 100), ("high", 2850), ("width", W(2750))));
            Assert.Equal(txWidth, Lexicon.Get("audio.filter_layer.tx_width", level, ("width", W(2750)), ("low", 100), ("high", 2850)));
            // Below 1 kHz the layer keeps its unit at Terse.
            if (level == VerbosityLevel.Terse)
                Assert.Equal("Width 500 hertz, 100 to 600", Lexicon.Get("audio.filter_layer.width", level, ("width", W(500)), ("low", 100), ("high", 600)));
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
            Assert.Equal("500", HertzSpeech.Width(500, HertzUnitStyle.Bare, level));
            Assert.Equal("950", HertzSpeech.Width(950, HertzUnitStyle.Bare, level));
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
