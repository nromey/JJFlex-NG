#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Every route that announces a filter preset says the preset's name
    /// once, then the filter in the ruled readout form (Noel, 2026-11-06,
    /// J15: "every Chatty filter readout"). Until round four of the
    /// 2026-11-06 junk box the preset routes said the name twice and no
    /// edges: "Example, 2750 hertz Example" (Codex's re-verify, F1).
    /// </summary>
    /// <remarks>
    /// Every expected string is a hand-written literal, assembled as the
    /// operator hears it, and DRAFT for Noel's keyboard audit.
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class FilterPresetAnnouncementTests
    {
        private static string Src(params string[] parts) => File.ReadAllText(Path.Combine(new[] { IntegrationPassTree.Root }.Concat(parts).ToArray()));

        private static readonly FilterPreset Example = new("Example", 100, 2850);

        [Theory]
        [InlineData("USB", VerbosityLevel.Chatty, "Example, filter 100 to 2850, width 2750 hertz")]
        [InlineData("USB", VerbosityLevel.Diagnostic, "Example, filter 100 to 2850, width 2750 hertz")]
        [InlineData("USB", VerbosityLevel.Terse, "Example, 2.75 k")]
        [InlineData("USB", VerbosityLevel.Critical, "Example, 2.75 k")]
        [InlineData("LSB", VerbosityLevel.Chatty, "Example, filter -2850 to -100, width 2750 hertz")]
        [InlineData("LSB", VerbosityLevel.Terse, "Example, 2.75 k")]
        public void A_preset_is_announced_by_name_once_then_the_filter(string mode, VerbosityLevel level, string heard)
            => Assert.Equal(heard, Example.Announcement(mode, level));

        [Theory]
        [InlineData(VerbosityLevel.Chatty, "Narrow, filter -125 to 125, width 250 hertz")]
        [InlineData(VerbosityLevel.Terse, "Narrow, 250 hertz")]   // #979: units on by default; J30's bare "250" is the off form
        public void A_narrow_cw_preset(VerbosityLevel level, string heard)
            => Assert.Equal(heard, new FilterPreset("Narrow", -125, 125).Announcement("CW", level));

        [Theory]
        [InlineData(VerbosityLevel.Chatty, "preset Example, filter 100 to 2850, width 2750 hertz")]
        [InlineData(VerbosityLevel.Terse, "preset Example, 2.75 k")]
        public void The_status_names_the_preset_once(VerbosityLevel level, string heard)
            => Assert.Equal(heard, Example.StatusSentence(100, 2850, level));

        /// <summary>
        /// The class, not the instance: no shipped preset, in any mode, at
        /// any level, says its own name twice.
        /// </summary>
        [Fact]
        public void No_shipped_preset_says_its_name_twice()
        {
            var presets = new FilterPresets();
            foreach (string mode in new[] { "USB", "LSB", "CW", "DIGU", "DIGL", "AM", "FM" })
                foreach (var p in presets.GetPresetsForMode(mode))
                    foreach (VerbosityLevel level in Enum.GetValues(typeof(VerbosityLevel)))
                    {
                        string said = p.Announcement(mode, level);
                        int times = Regex.Matches(said, @"\b" + Regex.Escape(p.Name) + @"\b").Count;
                        Assert.True(times == 1, $"{mode} {level}: \"{said}\" says \"{p.Name}\" {times} times.");
                        var (low, high) = FilterPresets.MirrorForMode(mode, p.Low, p.High);
                        string status = p.StatusSentence(low, high, level);
                        Assert.Equal(1, Regex.Matches(status, @"\b" + Regex.Escape(p.Name) + @"\b").Count);
                    }
        }

        /// <summary>
        /// The routes are pinned in FilterPresetAppliedTests since round five,
        /// where they speak what the filter BECAME; this keeps the check that
        /// the old fragment and its two wrappers stay gone.
        /// </summary>
        [Fact]
        public void Every_route_that_announces_a_preset_uses_the_one_announcement()
        {
            string handlers = Src("JJFlexWpf", "FreqOutHandlers.cs");
            string menu = Src("JJFlexWpf", "NativeMenuBar.cs");

            // The old fragment and its two wrappers are gone.
            foreach (string src in new[] { handlers, menu, Src("Radios", "FilterPresets.cs") })
                Assert.DoesNotContain("preset.FormatForSpeech()", src, StringComparison.Ordinal);
            Assert.DoesNotContain("settings.filter.preset_announce", Src("Radios", "Lexicon", "settings.json"), StringComparison.Ordinal);
            Assert.DoesNotContain("audio.filter.preset_selected", Src("Radios", "Lexicon", "audio.json"), StringComparison.Ordinal);
        }
    }
}
