using System;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The availability earcon's decision rule (#275), proven with an
    /// injected clock — the FrequencyEchoGuard pattern, no waiting.
    ///
    /// The rule under test: SOUND ONLY WHEN THE HELP CONTENT CHANGES, with
    /// the 1.5 second gap as a backstop rather than the primary rule. The
    /// cue is unintrusive because it is rare, not because it is quiet.
    /// </summary>
    public sealed class ContextHelpCueDeciderTests
    {
        private static readonly DateTime T0 =
            new(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);

        private static DateTime After(double seconds) => T0.AddSeconds(seconds);

        [Fact]
        public void Controls_with_no_help_never_cue()
        {
            var decider = new ContextHelpCueDecider();
            for (int i = 0; i < 5; i++)
            {
                Assert.False(decider.ShouldCue(null, After(i * 5)));
                Assert.False(decider.ShouldCue("", After(i * 5 + 1)));
                Assert.False(decider.ShouldCue("   ", After(i * 5 + 2)));
            }
        }

        [Fact]
        public void Five_controls_sharing_one_help_cue_once()
        {
            var decider = new ContextHelpCueDecider();
            int cues = 0;
            for (int i = 0; i < 5; i++)
                if (decider.ShouldCue("Shared panel help", After(i * 5))) cues++;
            Assert.Equal(1, cues);
        }

        [Fact]
        public void Changed_content_cues_again()
        {
            var decider = new ContextHelpCueDecider();
            Assert.True(decider.ShouldCue("Help for A", After(0)));
            Assert.True(decider.ShouldCue("Help for B", After(5)));
        }

        [Fact]
        public void A_bare_stretch_does_not_reset_the_memory()
        {
            // A -> no-help control -> A again: the operator already knows A
            // has help. Silence, per "five controls with no help must be
            // silent" generalized to the round trip.
            var decider = new ContextHelpCueDecider();
            Assert.True(decider.ShouldCue("Help for A", After(0)));
            Assert.False(decider.ShouldCue(null, After(5)));
            Assert.False(decider.ShouldCue("Help for A", After(10)));
        }

        [Fact]
        public void The_rate_limit_backstop_suppresses_without_forgetting()
        {
            var decider = new ContextHelpCueDecider();
            Assert.True(decider.ShouldCue("Help for A", After(0)));

            // New content inside the gap: suppressed by the backstop...
            Assert.False(decider.ShouldCue("Help for B", After(1.0)));

            // ...and NOT recorded as heard, so once the gap has passed the
            // same content may still cue. Suppression must not eat the cue.
            Assert.True(decider.ShouldCue("Help for B", After(2.0)));
        }

        [Fact]
        public void Content_the_operator_just_heard_via_ctrl_f1_does_not_cue()
        {
            var decider = new ContextHelpCueDecider();
            decider.NoteSpoken("Help for A");
            Assert.False(decider.ShouldCue("Help for A", After(10)));

            // NoteSpoken records content only — it never consumes the rate
            // limit, so different content cues immediately afterwards.
            Assert.True(decider.ShouldCue("Help for B", After(10.5)));
        }

        [Fact]
        public void NoteSpoken_ignores_silence()
        {
            var decider = new ContextHelpCueDecider();
            Assert.True(decider.ShouldCue("Help for A", After(0)));
            decider.NoteSpoken(null);
            decider.NoteSpoken("");
            // The memory still holds A: A stays silent, B cues.
            Assert.False(decider.ShouldCue("Help for A", After(10)));
            Assert.True(decider.ShouldCue("Help for B", After(20)));
        }
    }
}
