#nullable enable

using System;
using System.IO;
using System.Linq;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// A stored audio chain preset is announced in the J15 filter form with
    /// the word "preset" after its name (Noel, 2026-11-06 17:33, nap question
    /// J29): "Example preset, mic gain 50, filter 100 to 2850, width 2750
    /// hertz". It describes the STORED preset, so the edges are the stored
    /// ones. Terse is the short form, consistent with the filter presets, and
    /// under 1 kHz it is the bare number (J30). Every expected sentence is a
    /// literal, assembled as heard, and DRAFT.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class AudioPresetAnnouncementTests
    {
        private static AudioChainPreset Preset(int low, int high, string input = "")
            => new AudioChainPreset { Name = "Example", MicGain = 50, TxFilterLow = low, TxFilterHigh = high, RadioMicInput = input };

        [Theory]
        [InlineData(VerbosityLevel.Chatty, "Example preset, mic gain 50, filter 100 to 2850, width 2750 hertz")]
        [InlineData(VerbosityLevel.Diagnostic, "Example preset, mic gain 50, filter 100 to 2850, width 2750 hertz")]
        [InlineData(VerbosityLevel.Terse, "Example preset, mic gain 50, filter 2.75 k")]
        [InlineData(VerbosityLevel.Critical, "Example preset, mic gain 50, filter 2.75 k")]
        public void An_audio_preset_is_said_with_the_word_preset_and_the_filter_readout(VerbosityLevel level, string heard)
            => Assert.Equal(heard, Preset(100, 2850).FormatForSpeech(level));

        [Theory]
        [InlineData(VerbosityLevel.Chatty, "Example preset, mic gain 50, filter 300 to 800, width 500 hertz, tuned for MIC")]
        [InlineData(VerbosityLevel.Terse, "Example preset, mic gain 50, filter 500 hertz, tuned for MIC")]   // #979: units on by default
        public void A_narrow_preset_tuned_for_an_input(VerbosityLevel level, string heard)
            => Assert.Equal(heard, Preset(300, 800, "MIC").FormatForSpeech(level));

        /// <summary>Text on a window is not on the verbosity dial and is not changed by J29.</summary>
        [Fact]
        public void The_displayed_form_is_unchanged()
            => Assert.Equal("Example, mic gain 50, filter 2.75 kHz", Preset(100, 2850).FormatForDisplay());

        [Fact]
        public void The_import_announcement_uses_the_spoken_form()
        {
            string toolbar = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "Dialogs", "AudioWorkshopDialog.Toolbar.cs"));
            int at = toolbar.IndexOf("\"audio.preset.imported\"", StringComparison.Ordinal);
            Assert.True(at >= 0);
            Assert.StartsWith(", (\"preset\", preset.FormatForSpeech()))", toolbar.Substring(at + "\"audio.preset.imported\"".Length), StringComparison.Ordinal);
        }
    }
}
