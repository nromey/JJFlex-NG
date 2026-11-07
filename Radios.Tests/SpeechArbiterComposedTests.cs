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

        /// <summary>
        /// Round thirteen (Codex's API probe): a plain Latest replacing a
        /// waiting composed entry on the same key is what gets handed over.
        /// </summary>
        [Fact]
        public void A_plain_value_replacing_a_waiting_composition_is_what_is_handed_over()
        {
            _arbiter.LatestComposed("k", () => new SpeechComposition("composed lead", null), VerbosityLevel.Terse, "t");
            _arbiter.LatestComposed("k", () => new SpeechComposition("old composition", null), VerbosityLevel.Terse, "t");
            _arbiter.Latest("k", "new plain value", VerbosityLevel.Terse, SpeechCoalesceKind.Value, "t");
            _clock.Advance(5000);
            Assert.Equal(new[] { "composed lead", "new plain value" }, _handed);
        }

        /// <summary>
        /// Round thirteen (Codex's API probe): a composer that throws says
        /// nothing for its entry and never throws out, at once or on the
        /// clock's callback; later speech still works.
        /// </summary>
        [Fact]
        public void A_throwing_composer_says_nothing_and_throws_nothing()
        {
            System.Func<SpeechComposition?> broken = () => throw new System.InvalidOperationException("composer fault");
            _arbiter.LatestComposed("k", broken, VerbosityLevel.Terse, "t");                    // immediate
            _arbiter.LatestComposed("j", () => new SpeechComposition("lead", null), VerbosityLevel.Terse, "t");
            _arbiter.LatestComposed("j", broken, VerbosityLevel.Terse, "t");                    // at the flush
            _clock.Advance(5000);
            _arbiter.LatestComposed("j", () => new SpeechComposition("after", null), VerbosityLevel.Terse, "t");
            _clock.Advance(5000);
            Assert.Equal(new[] { "lead", "after" }, _handed);
        }

        /// <summary>
        /// Round thirteen (Codex's re-entrant case): a reader that reports a cut
        /// synchronously, while the arbiter is mid-emission, does not run the
        /// producer's callback under the arbiter's lock; it runs after.
        /// </summary>
        [Fact]
        public void An_outcome_reported_mid_emission_reaches_the_producer_outside_the_lock()
        {
            SpeechArbiter? arbiter = null;
            long next = 1;
            long? speaking = null;
            arbiter = new SpeechArbiter(_clock, () => VerbosityLevel.Chatty,
                (text, interrupt, intent, level, origin, salvaged) =>
                {
                    if (interrupt && speaking is long cut)
                    {
                        speaking = null;
                        arbiter!.OnOutcome(cut, "first", SpeechOutcome.Cancelled(0, 1, byUs: true, elapsedMs: 5));
                    }
                    long mine = next++;
                    speaking = mine;
                    return SpeechHandoff.TrackedAs(mine);
                },
                () => { }, (_, _, _, _) => { });
            object gate = typeof(SpeechArbiter).GetField("_lock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(arbiter)!;
            var heldWhenCalled = new List<bool>();
            arbiter.LatestComposed("k", () => new SpeechComposition("first", o => heldWhenCalled.Add(System.Threading.Monitor.IsEntered(gate))), VerbosityLevel.Terse, "t");
            arbiter.Emit("other", interrupt: true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t", subject: "other");
            _clock.Advance(1);
            Assert.Equal(new[] { false }, heldWhenCalled);
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
