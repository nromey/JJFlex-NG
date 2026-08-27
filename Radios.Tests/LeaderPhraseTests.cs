using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The other half of #206: the near-miss must be SHORT.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sprint 35 Track E made the near-miss name the neighbouring key, which was
    /// the hard part. It spoke the neighbour's full inventory description, and
    /// the longest of those runs to seventy-six characters — so the worst case
    /// was "Shift+O is not a command. O: Say what is still running and what it
    /// is costing — recording, captures, meter tones." Twenty words, delivered
    /// to someone who has just mistyped inside a modal layer and wants one fact.
    /// #206 asks for the opposite: "keep it short — this fires when someone has
    /// already made a mistake and does not want a paragraph."
    /// </para>
    /// <para>
    /// The real-inventory test at the bottom is the one that matters. The unit
    /// cases prove the rule; that one proves the rule is still true of the
    /// actual table, so a future chord with a chatty description fails here
    /// rather than quietly lengthening the recovery line.
    /// </para>
    /// </remarks>
    public class LeaderPhraseTests
    {
        [Theory]
        // Em dash: the tail qualifies, the head names.
        [InlineData("Turn PC audio on or off — whether radio audio plays through this computer",
                    "Turn PC audio on or off")]
        [InlineData("Say what is still running and what it is costing — recording, captures, meter tones",
                    "Say what is still running and what it is costing")]
        // Parenthetical.
        [InlineData("Arm or disarm the TX test tone (replaces your microphone while transmitting)",
                    "Arm or disarm the TX test tone")]
        [InlineData("Toggle On-Radio Neural Noise Reduction (the radio's own DSP)",
                    "Toggle On-Radio Neural Noise Reduction")]
        // Colon.
        [InlineData("Enter volume mode: pick a target letter, arrows adjust, Escape exits",
                    "Enter volume mode")]
        [InlineData("Mic check: speak your mic-audio verdict and level, nothing else", "Mic check")]
        // Nothing to cut.
        [InlineData("Toggle Noise Blanker", "Toggle Noise Blanker")]
        [InlineData("Speak log statistics", "Speak log statistics")]
        // A comma clause is NOT a qualifier — cutting at commas would amputate
        // half of what several of these descriptions actually say.
        [InlineData("Read the problems recorded this session",
                    "Read the problems recorded this session")]
        [InlineData("Say what is still running and what it is costing, roughly",
                    "Say what is still running and what it is costing, roughly")]
        // A hyphenated word is not an em dash.
        [InlineData("Toggle On-Radio Spectral Noise Reduction", "Toggle On-Radio Spectral Noise Reduction")]
        public void The_naming_half_survives_and_the_qualifier_goes(string description, string expected)
        {
            Assert.Equal(expected, LeaderPhrase.Brief(description));
        }

        [Fact]
        public void The_earliest_qualifier_wins_when_several_appear()
        {
            Assert.Equal("Do the thing",
                LeaderPhrase.Brief("Do the thing (with a note) — and a tail: and a clause"));
            Assert.Equal("Do the thing",
                LeaderPhrase.Brief("Do the thing: with a note (and a parenthetical)"));
        }

        [Fact]
        public void A_description_that_is_nothing_but_qualifier_comes_back_whole()
        {
            // Saying the long form beats saying nothing. An empty near-miss
            // would read as the key doing nothing at all, which is the exact
            // dead end #206 exists to remove.
            Assert.Equal("— all tail", LeaderPhrase.Brief("— all tail"));
            Assert.Equal(": all tail", LeaderPhrase.Brief(": all tail"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Nothing_in_nothing_out(string input)
        {
            Assert.Equal(input, LeaderPhrase.Brief(input));
        }

        [Fact]
        public void Every_real_leader_description_briefs_to_a_line_you_can_say_in_one_breath()
        {
            // 48 characters is the current worst case with room above it, not a
            // number chosen to fit. It is roughly "Capture a noise profile for
            // PC Spectral NR" — long enough for any command in this layer to
            // name itself, short enough that the whole sentence stays under
            // twelve words once "Ctrl+G is not a command. G: " is in front.
            const int limit = 52;

            var entries = LeaderSourceScan.RealInventoryEntries();
            Assert.True(entries.Count >= 25,
                $"only {entries.Count} inventory entries read — the scanner is broken and this "
                + "test would pass on an empty table");

            var tooLong = entries
                .Select(e => new { e.Display, Brief = LeaderPhrase.Brief(e.Description) })
                .Where(x => x.Brief.Length > limit)
                .Select(x => $"{x.Display} → \"{x.Brief}\" ({x.Brief.Length} chars)")
                .ToList();

            Assert.True(tooLong.Count == 0,
                "These leader descriptions are still a paragraph after briefing, and the "
                + "near-miss says them to someone who has just mistyped inside the layer "
                + "(#206). Either shorten the naming half of the description, or move the "
                + "detail behind an em dash, a parenthesis or a colon where LeaderPhrase "
                + "will cut it: " + string.Join("; ", tooLong));
        }
    }
}
