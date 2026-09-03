using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The one rule for what a refusal SAYS beside its tone (#528), pinned
    /// so that the value-layer engine and the leader dispatcher — which both
    /// consult it — cannot drift apart.
    /// </summary>
    /// <remarks>
    /// Ruled by Noel 2026-09-02: scale by VERBOSITY, never by experience.
    /// Chatty gets the teaching sentence; below Chatty the tone is the whole
    /// answer; and when the tone cannot sound the words stand in at every
    /// level, because a refused key that produces nothing at all is the
    /// invisible failure. The engine's own tests exercise the rule end to
    /// end; this file is the rule itself, in one place.
    /// </remarks>
    public class RefusalVoiceTests
    {
        [Fact]
        public void At_chatty_the_words_are_always_spoken()
        {
            Assert.False(RefusalVoice.ToneStandsAlone(VerbosityLevel.Chatty, toneWillSound: true));
            Assert.False(RefusalVoice.ToneStandsAlone(VerbosityLevel.Chatty, toneWillSound: false));
        }

        [Theory]
        [InlineData(VerbosityLevel.Terse)]
        [InlineData(VerbosityLevel.Critical)]
        public void Below_chatty_the_tone_is_the_whole_answer(VerbosityLevel level)
        {
            Assert.True(RefusalVoice.ToneStandsAlone(level, toneWillSound: true));
        }

        [Theory]
        [InlineData(VerbosityLevel.Terse)]
        [InlineData(VerbosityLevel.Critical)]
        public void Below_chatty_the_words_return_when_the_tone_cannot_sound(VerbosityLevel level)
        {
            // Earcons off, category off, or no tone wired at all: silence is
            // the one outcome never allowed, so the words come back.
            Assert.False(RefusalVoice.ToneStandsAlone(level, toneWillSound: false));
        }

        [Fact]
        public void Diagnostic_is_above_chatty_and_keeps_the_words()
        {
            // Diagnostic is opt-in from Settings and narrates MORE, never
            // less; a tester chasing something wants every sentence.
            Assert.False(RefusalVoice.ToneStandsAlone(VerbosityLevel.Diagnostic, toneWillSound: true));
        }

        [Fact]
        public void A_lower_level_never_says_more_than_a_higher_one()
        {
            // The monotonic property the verbosity control promises: turning
            // it DOWN never makes the app talk more. Expressed on the rule as
            // "once the tone stands alone, it keeps standing alone below".
            bool previous = false;
            foreach (var level in new[] { VerbosityLevel.Diagnostic, VerbosityLevel.Chatty, VerbosityLevel.Terse, VerbosityLevel.Critical })
            {
                bool now = RefusalVoice.ToneStandsAlone(level, toneWillSound: true);
                Assert.True(now || !previous, level + " speaks again after a higher level fell silent");
                previous = now;
            }
        }
    }
}
