#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The 2026-11-06 integration pass's finding 10 (#747): the width readouts
    /// the first round missed go through the one formatter, a dialog body is
    /// never put on the verbosity dial, and the scan that guards the class
    /// reads the tracked tree in every source language.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class HertzSpeechIntegrationTests
    {
        private static string Src(params string[] parts) => File.ReadAllText(Path.Combine(new[] { IntegrationPassTree.Root }.Concat(parts).ToArray()));

        private static string Statement(string text, string start)
        {
            int at = text.IndexOf(start, StringComparison.Ordinal);
            Assert.True(at >= 0, start + " is not where this test reads it.");
            return text.Substring(at, text.IndexOf(';', at) - at + 1);
        }

        /// <summary>
        /// The filter layer's own width readout spoke raw hertz at every
        /// verbosity ("Width 2750") while its report beside it said "2.75
        /// kilohertz" at Terse. Both now say the width the same way.
        /// </summary>
        [Fact]
        public void The_filter_layers_width_readout_speaks_through_the_shared_formatter()
        {
            string keys = Src("JJFlexWpf", "KeyCommands.cs");
            // A filter readout's width since the J13 ruling (2026-11-06):
            // "2.75 k" at Terse, "width 2750 hertz" at Chatty (J15), with the
            // sentence itself on the verbosity dial.
            string rx = Statement(keys, "rx.Width = () =>");
            Assert.Contains("Radios.HertzSpeech.ReadoutWidth(rx.Bank.Width, Radios.HertzUnitStyle.KiloLetter)", rx, StringComparison.Ordinal);
            Assert.Contains("Radios.ScreenReaderOutput.CurrentVerbosity", rx, StringComparison.Ordinal);
            string tx = Statement(keys, "tx.Width = () =>");
            Assert.Contains("Radios.HertzSpeech.ReadoutWidth(tx.Bank.Width, Radios.HertzUnitStyle.KiloLetter)", tx, StringComparison.Ordinal);
            Assert.Contains("Radios.ScreenReaderOutput.CurrentVerbosity", tx, StringComparison.Ordinal);
        }

        /// <summary>
        /// Every filter READOUT, the ones that say the edges and then the
        /// width, takes its width from <see cref="HertzSpeech.ReadoutWidth(int, HertzUnitStyle)"/>:
        /// the bracket keys, the layer's report and the Ctrl+J readouts
        /// (J15, Noel 2026-11-06: "Filter 100 to 2900, width 2800 hertz").
        /// </summary>
        [Fact]
        public void Every_filter_readout_takes_the_readout_width()
        {
            string handlers = Src("JJFlexWpf", "FreqOutHandlers.cs");
            Assert.Contains("string widthStr = Radios.HertzSpeech.ReadoutWidth(high - low, Radios.HertzUnitStyle.Bare);", handlers, StringComparison.Ordinal);
            string keys = Src("JJFlexWpf", "KeyCommands.cs");
            Assert.Contains("(\"width\", Radios.HertzSpeech.ReadoutWidth(high - low, Radios.HertzUnitStyle.KiloLetter)));",
                Statement(keys, "private static string FilterReport(string key, int low, int high)"), StringComparison.Ordinal);
        }

        /// <summary>
        /// Home's read-only receive filter field used KiloDigits with its own
        /// 1000 Hz test and its own pair of strings; it shows the shared
        /// displayed form now.
        /// </summary>
        [Fact]
        public void Homes_receive_filter_width_shows_the_shared_displayed_form()
        {
            string panel = Src("JJFlexWpf", "Controls", "ScreenFieldsPanel.xaml.cs");
            Assert.Contains("Radios.HertzSpeech.DisplayedWidth(filterWidth)", panel, StringComparison.Ordinal);
            Assert.DoesNotContain("rx_filter_width_khz", panel, StringComparison.Ordinal);
        }

        /// <summary>The filter calculator shows its width in the shared displayed form, not as a bare integer.</summary>
        [Fact]
        public void The_filter_calculator_shows_the_shared_displayed_form()
        {
            string calc = Src("JJFlexWpf", "Dialogs", "FilterCalculatorDialog.xaml.cs");
            Assert.Contains("(\"width\", Radios.HertzSpeech.DisplayedWidth(width))", Statement(calc, "ResultText.Text = Lexicon.Get(\"audio.filter.calculator.computed_width\""), StringComparison.Ordinal);
            Assert.Contains("(\"width\", Radios.HertzSpeech.DisplayedWidth(high - low))", Statement(calc, "ResultText.Text = Lexicon.Get(\"audio.filter.calculator.all_three\""), StringComparison.Ordinal);
        }

        /// <summary>
        /// KiloDigits is the formatter's own building block. A caller outside
        /// it is a second kilo rule.
        /// </summary>
        [Fact]
        public void Nothing_outside_the_formatter_builds_its_own_kilo_digits()
        {
            var callers = IntegrationPassTree.AuthoredSource
                .Concat(IntegrationPassTree.UntrackedSource())
                .Where(f => !IntegrationPassTree.IsTest(f))
                .Select(IntegrationPassTree.Relative)
                .Where(r => r.Replace('\\', '/') != "Radios/HertzSpeech.cs")
                .Where(r => File.ReadAllText(Path.Combine(IntegrationPassTree.Root, r)).Contains("KiloDigits(", StringComparison.Ordinal))
                .ToList();
            Assert.Empty(callers);
        }

        /// <summary>
        /// The Audio Workshop's delete confirmation is a dialog body, read on
        /// arrival, not speech: it shows the preset in the displayed form,
        /// whatever the verbosity. It used the spoken form, which put a window
        /// body on the verbosity dial.
        /// </summary>
        [Fact]
        public void A_presets_delete_confirmation_shows_the_displayed_form()
        {
            string toolbar = Src("JJFlexWpf", "Dialogs", "AudioWorkshopDialog.Toolbar.cs");
            string body = Statement(toolbar, "Lexicon.Get(\"audio.preset.delete_body\"");
            Assert.Contains("(\"preset\", preset.FormatForDisplay())", body, StringComparison.Ordinal);
            Assert.DoesNotContain("FormatForSpeech", body, StringComparison.Ordinal);

            var preset = new AudioChainPreset { Name = "Rag", MicGain = 5, TxFilterLow = 100, TxFilterHigh = 2850 };
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
            try
            {
                Assert.Contains("2.75 kHz", preset.FormatForDisplay(), StringComparison.Ordinal);
                Assert.DoesNotContain("2.75 kHz", preset.FormatForSpeech(), StringComparison.Ordinal);
            }
            finally
            {
                ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
            }
        }

        /// <summary>
        /// The width scan reads the TRACKED tree (git ls-files, plus untracked
        /// source not yet ignored), not whatever is on the disk, and every
        /// source language that could format a width, VB included.
        /// </summary>
        [Fact]
        public void The_width_scan_reads_the_tracked_tree_in_every_source_language()
        {
            string scan = Src("Radios.Tests", "HertzSpeechTests.cs");
            int at = scan.IndexOf("public void No_filter_width_is_formatted_in_kilohertz_outside_the_shared_formatter()", StringComparison.Ordinal);
            Assert.True(at > 0);
            string body = scan.Substring(at, scan.IndexOf("private static string RepoRoot()", at) - at);
            Assert.Contains("IntegrationPassTree.AuthoredSource", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Directory.EnumerateFiles", body, StringComparison.Ordinal);
            Assert.Contains(IntegrationPassTree.AuthoredSource, f => f.EndsWith(".vb", StringComparison.OrdinalIgnoreCase));
            // The VB shape is recognised too.
            Assert.True(HertzSpeechTests.IsHandRolledKiloWidth("Dim w As String = Format(width / 1000, \"0.0\")"));
        }
    }
}
