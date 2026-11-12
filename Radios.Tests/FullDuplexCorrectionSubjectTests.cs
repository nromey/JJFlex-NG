#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Junk box batch 5 repair two (the integration pass's F3, #1035). Since
    /// #1035 the Full duplex outcome is QUEUED behind Home's arrival, so the
    /// LAN check, started when that outcome is handed over, can disagree while
    /// "Full duplex on" still waits. The correction interrupts; without a
    /// subject the arbiter rescues the interrupted outcome one settle window
    /// later, and the last thing the operator hears is the opposite of the
    /// radio's state. The correction now carries the outcome's own subject,
    /// which retires it.
    /// </summary>
    /// <remarks>
    /// The real arbiter on a virtual clock, fed exactly what the two producers
    /// emit: <c>FullDuplexControl.Deliver</c>'s queued outcome on
    /// <see cref="SpeechSubject.FullDuplexOutcome"/>, and the correction as
    /// <see cref="FlexBase.FullDuplexCorrection"/> describes it, spoken as
    /// <c>ScreenReaderOutput.Speak(level, interrupt: true, subject)</c> hands
    /// it to the arbiter (no intent). The LAN check reaching its disagreement
    /// branch is driven through the real rig by
    /// <c>ListenbackRoundSixTests.Lan_check_follows_the_control_result_and_updates_only_on_a_disagreement</c>;
    /// that the speak call passes the described subject is pinned by source
    /// below. Whether NVDA then says the correction last is for the harness.
    /// The lexicon load reads the language store under the settings root, so
    /// the class sits in the settings-root collection.
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class FullDuplexCorrectionSubjectTests
    {
        private sealed record SinkCall(string Message, bool Salvaged, double AtMs);

        private readonly FakeSpeechClock _clock = new();
        private readonly List<SinkCall> _calls = new();

        public FullDuplexCorrectionSubjectTests() => Lexicon.Load(Lexicon.Partitions);

        private SpeechArbiter NewArbiter() => new SpeechArbiter(
            _clock,
            () => VerbosityLevel.Critical,   // Off: both sentences are Critical
            (message, interrupt, intent, level, origin, salvaged) =>
            {
                _calls.Add(new SinkCall(message, salvaged, _clock.ElapsedMs));
                return true;
            },
            () => { },
            (message, level, intent, origin) => { });

        /// <summary>
        /// Codex's replay (N1 on #1035): the outcome queued, the correction
        /// interrupting 10 ms later, then the settle window and a second
        /// besides. Returns what the reader was handed, in order.
        /// </summary>
        private List<SinkCall> Replay(string? correctionSubject)
        {
            var a = NewArbiter();
            a.Emit(Lexicon.Get("audio.full_duplex.on"), false, SpeechIntent.Queue, VerbosityLevel.Critical,
                "FullDuplexControl.Deliver", SpeechSubject.FullDuplexOutcome);
            _clock.Advance(10);
            var correction = FlexBase.FullDuplexCorrection(radioReportsOn: false);
            a.Emit(Lexicon.Get(correction.Key), true, null, VerbosityLevel.Critical,
                "FlexBase.VerifyFullDuplexLanAsync", correctionSubject);
            _clock.Advance(SpeechArbiter.SalvageSettleMs + 1000);
            return _calls.ToList();
        }

        [Fact]
        public void Without_the_subject_the_stale_outcome_is_rescued_after_the_correction()
        {
            // The control: the correction as it was spoken before the repair,
            // with no subject. The interrupted "Full duplex on" comes back
            // after "Full duplex is off", so the test can see the defect.
            var heard = Replay(correctionSubject: null);
            Assert.Contains(heard, c => c.Salvaged && c.Message == Lexicon.Get("audio.full_duplex.on"));
            Assert.Equal(Lexicon.Get("audio.full_duplex.on"), heard[^1].Message);
        }

        [Fact]
        public void The_correction_retires_the_queued_outcome_so_it_is_heard_last()
        {
            var correction = FlexBase.FullDuplexCorrection(radioReportsOn: false);
            var heard = Replay(correction.Subject);
            Assert.DoesNotContain(heard, c => c.Salvaged && c.Message == Lexicon.Get("audio.full_duplex.on"));
            Assert.Equal(Lexicon.Get(correction.Key), heard[^1].Message);
        }

        [Fact]
        public void The_correction_names_the_state_the_radio_reports()
        {
            Assert.Equal("audio.full_duplex.disagreed_on", FlexBase.FullDuplexCorrection(true).Key);
            Assert.Equal("audio.full_duplex.disagreed_off", FlexBase.FullDuplexCorrection(false).Key);
            Assert.Equal(SpeechSubject.FullDuplexOutcome, FlexBase.FullDuplexCorrection(true).Subject);
        }

        [Fact]
        public void The_LAN_check_speaks_the_described_correction_with_its_subject()
        {
            string src = Source("Radios", "FlexBase.FullDuplex.cs");
            int at = src.IndexOf("private async Task VerifyFullDuplexLanAsync(", StringComparison.Ordinal);
            Assert.True(at >= 0, "VerifyFullDuplexLanAsync is not where this test reads it.");
            int end = src.IndexOf("catch (OperationCanceledException)", at, StringComparison.Ordinal);
            string body = src.Substring(at, end - at);
            Assert.Contains("var correction = FullDuplexCorrection(actual.Value);", body, StringComparison.Ordinal);
            Assert.Contains("ScreenReaderOutput.Speak(Lexicon.Get(correction.Key),", body, StringComparison.Ordinal);
            Assert.Contains("interrupt: true, subject: correction.Subject);", body, StringComparison.Ordinal);
            // One sentence on this branch, and no literal key beside the helper's.
            Assert.DoesNotContain("\"audio.full_duplex.disagreed_", body, StringComparison.Ordinal);
        }

        private static string Source(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray())).Replace("\r\n", "\n");
        }
    }
}
