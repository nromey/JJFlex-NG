#nullable enable

using System.Collections.Generic;
using Radios;
using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The arbiter's composed Latest entry (#747, round twelve of the
    /// 2026-11-06 junk box): the sentence is built when it is handed over, null
    /// means nothing to say, and the producer hears the reader's outcome for a
    /// tracked hand-over only, once.
    /// </summary>
    public sealed class SpeechArbiterComposedTests
    {
        private readonly FakeSpeechClock _clock = new FakeSpeechClock();
        private readonly List<string> _handed = new List<string>();
        private readonly SpeechArbiter _arbiter;
        private long _next = 1;
        private bool _tracked = true;
        private VerbosityLevel _verbosity = VerbosityLevel.Chatty;

        public SpeechArbiterComposedTests()
        {
            _arbiter = new SpeechArbiter(_clock, () => _verbosity,
                (text, interrupt, intent, level, origin, salvaged) =>
                {
                    _handed.Add(text);
                    return _tracked ? SpeechHandoff.TrackedAs(_next++) : SpeechHandoff.Untracked;
                },
                () => { }, (_, _, _, _) => { });
        }

        [Fact]
        public void A_waiting_entry_is_composed_when_it_is_handed_over_not_when_submitted()
        {
            int value = 1;
            _arbiter.LatestComposed("k", () => new SpeechComposition("first " + value, null), VerbosityLevel.Terse, "t");
            _arbiter.LatestComposed("k", () => new SpeechComposition("value " + value, null), VerbosityLevel.Terse, "t");
            value = 3;   // changes while the second waits
            _clock.Advance(5000);
            Assert.Equal(new[] { "first 1", "value 3" }, _handed);
        }

        [Fact]
        public void Null_says_nothing()
        {
            _arbiter.LatestComposed("k", () => null, VerbosityLevel.Terse, "t");
            _clock.Advance(5000);
            Assert.Empty(_handed);
        }

        [Fact]
        public void The_producer_hears_a_tracked_outcome_once()
        {
            var outcomes = new List<SpeechOutcomeKind>();
            _arbiter.LatestComposed("k", () => new SpeechComposition("hello", o => outcomes.Add(o.Kind)), VerbosityLevel.Terse, "t");
            _arbiter.OnOutcome(1, "hello", SpeechOutcome.Completed(1, 1, 50));
            _arbiter.OnOutcome(1, "hello", SpeechOutcome.Completed(1, 1, 50));
            Assert.Equal(new[] { SpeechOutcomeKind.Completed }, outcomes);
        }

        [Fact]
        public void An_untracked_hand_over_never_reports_completed()
        {
            _tracked = false;
            var outcomes = new List<SpeechOutcomeKind>();
            _arbiter.LatestComposed("k", () => new SpeechComposition("hello", o => outcomes.Add(o.Kind)), VerbosityLevel.Terse, "t");
            _arbiter.OnOutcome(0, "hello", SpeechOutcome.Completed(1, 1, 50));
            Assert.Single(_handed);
            Assert.Empty(outcomes);
        }

        /// <summary>Gated by verbosity at hand-over, like any Latest value.</summary>
        [Fact]
        public void A_composed_sentence_above_the_verbosity_setting_is_not_handed_over()
        {
            _verbosity = VerbosityLevel.Critical;
            _arbiter.LatestComposed("k", () => new SpeechComposition("hello", null), VerbosityLevel.Terse, "t");
            _clock.Advance(5000);
            Assert.Empty(_handed);
        }

        /// <summary>The plain Latest path is unchanged: a repeat of the key's last value is still skipped.</summary>
        [Fact]
        public void Control_a_plain_latest_repeat_is_still_skipped()
        {
            _arbiter.Latest("k", "same", VerbosityLevel.Terse, SpeechCoalesceKind.Value, "t");
            _arbiter.Latest("k", "same", VerbosityLevel.Terse, SpeechCoalesceKind.Value, "t");
            _clock.Advance(5000);
            Assert.Equal(new[] { "same" }, _handed);
        }
    }
}
