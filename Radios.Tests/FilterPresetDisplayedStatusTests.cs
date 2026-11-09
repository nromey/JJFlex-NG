using System;
using System.IO;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The Status window shows the active filter preset in a form that does
    /// not follow the speech verbosity dial (#983); Speak Status keeps the
    /// dialled sentence.
    /// </summary>
    /// <remarks>
    /// The 2026-11-06 batch's rule: displayed text stays off the dial. The
    /// Status window was handed the Speak Status sentence, so turning
    /// verbosity down changed what the window SHOWED ("preset Wide, 2.68 k" at
    /// Terse, the edges and "width 2680 hertz" at Chatty).
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public class FilterPresetDisplayedStatusTests
    {
        // A preset recognised within 20 Hz an edge, with live edges that differ
        // from the saved ones, as the round-five fix requires them to be said.
        private const int LiveLow = 110, LiveHigh = 2790;

        private static FilterPresets Presets()
        {
            var p = new FilterPresets();
            p.Modes.Add(new ModePresets { Mode = "SSB", Presets = { new FilterPreset("Wide", 100, 2800) } });
            return p;
        }

        private static T AtVerbosity<T>(VerbosityLevel level, Func<T> body)
        {
            var saved = ScreenReaderOutput.CurrentVerbosity;
            ScreenReaderOutput.CurrentVerbosity = level;
            try { return body(); }
            finally { ScreenReaderOutput.CurrentVerbosity = saved; }
        }

        [Fact]
        public void The_displayed_form_is_the_same_at_every_verbosity()
        {
            var p = Presets();
            string terse = AtVerbosity(VerbosityLevel.Terse, () => p.ActiveDisplayedStatus("USB", LiveLow, LiveHigh)!);
            string chatty = AtVerbosity(VerbosityLevel.Chatty, () => p.ActiveDisplayedStatus("USB", LiveLow, LiveHigh)!);
            Assert.Equal(terse, chatty);
            // The name, the LIVE edges, and the never-rounding displayed width.
            Assert.Contains("Wide", terse, StringComparison.Ordinal);
            Assert.Contains("110", terse, StringComparison.Ordinal);
            Assert.Contains("2790", terse, StringComparison.Ordinal);
            Assert.Contains(HertzSpeech.DisplayedWidth(LiveHigh - LiveLow, HertzUnitStyle.KiloLetter), terse, StringComparison.Ordinal);
        }

        [Fact]
        public void Control_the_spoken_sentence_still_follows_the_dial()
        {
            var p = Presets();
            string terse = p.ActiveStatus("USB", LiveLow, LiveHigh, VerbosityLevel.Terse)!;
            string chatty = p.ActiveStatus("USB", LiveLow, LiveHigh, VerbosityLevel.Chatty)!;
            Assert.NotEqual(terse, chatty);
        }

        [Fact]
        public void No_named_preset_shows_nothing_as_before()
        {
            Assert.Null(Presets().ActiveDisplayedStatus("USB", 300, 1500));
        }

        [Fact]
        public void The_status_window_takes_the_displayed_form_and_Speak_Status_the_spoken_one()
        {
            string root = CaptureMeterSetTests.RepoRoot();
            string commands = File.ReadAllText(Path.Combine(root, "JJFlexWpf", "KeyCommands.cs"));
            Assert.Contains("FilterPresetStatus = mw == null ? null : mw.GetFilterPresetDisplayedStatus,", commands, StringComparison.Ordinal);
            Assert.Contains("var filterPreset = mw?.GetFilterPresetStatus();", commands, StringComparison.Ordinal);
            string handlers = File.ReadAllText(Path.Combine(root, "JJFlexWpf", "FreqOutHandlers.cs"));
            Assert.Contains("return FilterPresets.ActiveDisplayedStatus(mode, low, high);", handlers, StringComparison.Ordinal);
        }
    }
}
