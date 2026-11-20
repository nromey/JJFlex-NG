#nullable enable
using System;
using System.Threading.Tasks;
using Radios;
using Radios.Speech;
using Xunit;
using Xunit.Abstractions;

namespace Radios.Tests
{
    /// <summary>
    /// The audition's "Recording" must be HEARD IN FULL before the microphone
    /// opens (Sprint 49 Track Q3 repair 1; Codex's check of 83932446, C2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Adopted from Codex's reproduction (<c>Q3ReviewSpeechTests</c>, in the
    /// check's evidence): the REAL <see cref="SpeechArbiter"/>, the REAL
    /// <see cref="CountdownSpeech"/> and the REAL <see cref="SaidInFull"/>, with
    /// "Recording" cut at word mark 1 of 1, by us and by something else. On
    /// 83932446 both cases opened the take, with the beeps off as well, because
    /// "Recording" was followed through the count's own handle, which counts a
    /// one-word piece cut part-way as heard. The window now uses
    /// <see cref="CountdownSpeech.SayInFull"/>. The count's own rule is
    /// unchanged, and a test below says so.
    /// </para>
    /// <para>No speech is produced: the arbiter's emitter is a stand-in that hands back a ticket.</para>
    /// </remarks>
    public sealed class RecordingHeardInFullTests
    {
        private readonly ITestOutputHelper _out;
        public RecordingHeardInFullTests(ITestOutputHelper output) { _out = output; }

        private sealed class Rig
        {
            public readonly FakeSpeechClock Clock = new FakeSpeechClock();
            public long Ticket;
            public readonly SpeechArbiter Arbiter;
            public readonly CountdownSpeech Speech;

            public Rig(bool tracked = true)
            {
                Arbiter = new SpeechArbiter(Clock, () => VerbosityLevel.Critical,
                    (message, interrupt, intent, level, origin, salvaged) =>
                        tracked ? SpeechHandoff.TrackedAs(++Ticket) : SpeechHandoff.Untracked,
                    () => { }, (message, level, intent, origin) => { });
                Speech = new CountdownSpeech(Arbiter, _ => true);
            }
        }

        private static Task<SaidInFull.Outcome> Wait(ICountdownWords words, bool onlyWarning) =>
            SaidInFull.SayAsync((_, _) => words, _ => { }, "Recording", onlyWarning, () => true,
                () => 9000, (_, _) => throw new InvalidOperationException("must not wait: the outcome is already known"));

        [Theory]
        [InlineData(false, true)]
        [InlineData(true, true)]
        [InlineData(false, false)]
        [InlineData(true, false)]
        public async Task Recording_cut_after_its_first_word_mark_is_not_said_in_full_and_opens_nothing(bool byUs, bool onlyWarning)
        {
            var rig = new Rig();
            var words = rig.Speech.SayInFull("Recording", onlyWarning, SpeechSubject.ComputerRecorder);
            Assert.False(words.Finished);
            rig.Arbiter.OnOutcome(rig.Ticket, "Recording", SpeechOutcome.Cancelled(1, 1, byUs, 10));
            _out.WriteLine($"Recording cancelled at its first word mark after 10 ms; byUs={byUs}; Finished={words.Finished}; Failed={words.Failed}");

            Assert.False(words.Finished);
            Assert.True(words.Failed);
            var outcome = await Wait(words, onlyWarning);
            _out.WriteLine($"SaidInFull outcome={outcome}; opens={SaidInFull.LetsItOpen(outcome)}");
            Assert.NotEqual(SaidInFull.Outcome.Said, outcome);
            Assert.False(SaidInFull.LetsItOpen(outcome));
        }

        [Fact]
        public async Task Recording_the_reader_reports_completed_is_said_and_opens()
        {
            // The positive control: the same road opens the take when the word is whole.
            var rig = new Rig();
            var words = rig.Speech.SayInFull("Recording", onlyWarning: true, SpeechSubject.ComputerRecorder);
            Assert.False(words.Finished);
            rig.Arbiter.OnOutcome(rig.Ticket, "Recording", SpeechOutcome.Completed(1, 1, 400));
            Assert.True(words.Finished);
            Assert.False(words.Failed);
            Assert.Equal(SaidInFull.Outcome.Said, await Wait(words, onlyWarning: true));
        }

        [Fact]
        public void A_cut_Recording_said_again_whole_afterwards_is_still_not_heard_in_full()
        {
            // The first hearing was cut; a whole second hearing does not make it whole.
            var rig = new Rig();
            var words = rig.Speech.SayInFull("Recording", onlyWarning: false, SpeechSubject.ComputerRecorder);
            rig.Arbiter.OnOutcome(rig.Ticket, "Recording", SpeechOutcome.Cancelled(1, 1, true, 10));
            rig.Arbiter.OnOutcome(rig.Ticket, "Recording", SpeechOutcome.Completed(1, 1, 400));
            Assert.False(words.Finished);
            Assert.True(words.Failed);
        }

        [Fact]
        public void Recording_withdrawn_before_the_reader_finished_it_is_never_said()
        {
            var rig = new Rig();
            var words = rig.Speech.SayInFull("Recording", onlyWarning: false, SpeechSubject.ComputerRecorder);
            rig.Arbiter.Supersede(SpeechSubject.ComputerRecorder, "the start being called off", "test");
            Assert.True(words.Failed);
            Assert.False(words.Finished);
        }

        [Fact]
        public void The_countdowns_own_rule_is_unchanged_a_number_cut_part_way_counts_as_heard()
        {
            // Do NOT change the countdown's semantics (the repair brief): its
            // handle still counts a one-word piece cut part-way as heard.
            var rig = new Rig();
            var number = rig.Speech.Say("3", onlyWarning: true);
            rig.Arbiter.OnOutcome(rig.Ticket, "3", SpeechOutcome.Cancelled(1, 1, true, 10));
            Assert.True(number.Finished);
            Assert.False(number.Failed);
        }

        [Fact]
        public void On_the_estimate_path_Recording_is_held_to_its_estimate_and_the_margin_where_it_is_the_only_warning()
        {
            // No completion channel: it cannot report a cut, so it is held as
            // the count holds an untracked word (unchanged, and stated).
            var rig = new Rig(tracked: false);
            var alone = rig.Speech.SayInFull("Recording", onlyWarning: true, SpeechSubject.ComputerRecorder);
            Assert.False(alone.Finished);
            rig.Clock.Advance(CountdownSpeech.UntrackedMarginFloorMs - 1);
            Assert.False(alone.Finished);
            rig.Clock.Advance(5000);
            Assert.True(alone.Finished);
            Assert.False(alone.Failed);
        }
    }
}
