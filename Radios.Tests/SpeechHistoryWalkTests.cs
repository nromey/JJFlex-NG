using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The speech-history walk's cursor arithmetic, both directions (#433).
    /// </summary>
    public class SpeechHistoryWalkTests
    {
        private const int Ten = 10;

        [Fact]
        public void Forward_from_the_newest_wraps_instead_of_throwing()
        {
            // THE reason this arithmetic is extracted and pinned. Stepping
            // toward the newer end from cursor 0 is -1 before the modulus, and
            // in C# -1 % 10 is -1, not 9. Indexing a list at -1 throws — on a
            // background thread, where that is silent process death rather
            // than a message.
            Assert.Equal(9, ScreenReaderOutput.StepCursor(0, Ten, older: false, stale: false));
        }

        [Fact]
        public void Back_from_the_oldest_wraps_to_the_newest()
        {
            // The documented behaviour: running off the end comes round rather
            // than stopping, because a silent dead end is indistinguishable
            // from a broken key.
            Assert.Equal(0, ScreenReaderOutput.StepCursor(9, Ten, older: true, stale: false));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_stale_walk_starts_at_the_newest_whichever_way_you_press(bool older)
        {
            // One rule, not two: the first press after a pause says the most
            // recent thing, and the direction only starts mattering on the
            // second press.
            Assert.Equal(0, ScreenReaderOutput.StepCursor(4, Ten, older, stale: true));
        }

        [Fact]
        public void An_uninitialised_cursor_starts_at_the_newest()
        {
            Assert.Equal(0, ScreenReaderOutput.StepCursor(-1, Ten, older: true, stale: false));
        }

        [Fact]
        public void An_empty_history_never_produces_an_index_to_read()
        {
            // Guard for the order of checks in the callers: they test Count
            // before calling, but a zero here must not compute a modulus by
            // zero if that order ever changes.
            Assert.Equal(0, ScreenReaderOutput.StepCursor(0, 0, older: true, stale: false));
            Assert.Equal(0, ScreenReaderOutput.StepCursor(0, 0, older: false, stale: false));
        }

        [Fact]
        public void The_two_directions_are_inverses_across_the_whole_ring()
        {
            // Walk back one and forward one from every position and land where
            // you started. This is what makes "overshoot and come back" work,
            // which is the entire reason forward exists.
            for (int i = 0; i < Ten; i++)
            {
                int back = ScreenReaderOutput.StepCursor(i, Ten, older: true, stale: false);
                Assert.Equal(i, ScreenReaderOutput.StepCursor(back, Ten, older: false, stale: false));
            }
        }
    }
}
