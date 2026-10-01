#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    // ────────────────────────────────────────────────────────────────
    //  A protected safety sentence that claims a PRESENT state carries
    //  its producer's answer to "is that still the state", and is asked
    //  before every automatic replay (Astra's Track IJK review, blocker 6;
    //  #617: each fact carries the condition that makes it true).
    //
    //  The case Astra traced: the reflected-power cut says "you are no
    //  longer on the air"; an unheard copy is retained; the operator keys
    //  again; an unrelated interrupt rescues the copy and it is replayed
    //  DURING the new transmission. The finite retry count did not make the
    //  claim true. Noel's 2026-09-30 ruling ships the alarms on their own
    //  route and defers the fact store; it does not waive a false
    //  operator-facing claim, and this is the half of the remedy that fits
    //  on the own route: a producer validity contract, with the trace as the
    //  record. The reachable history surface stays the fact store's.
    // ────────────────────────────────────────────────────────────────
    public class SafetyValidityTests
    {
        private sealed record SinkCall(string Message, bool Salvaged, long Ticket);

        private readonly FakeSpeechClock _clock = new();
        private readonly List<SinkCall> _calls = new();
        private long _nextTicket;

        private const int Settle = SpeechArbiter.SalvageSettleMs;
        private const string Cut = "Transmit stopped. 80 percent of your power is coming back on ANT2. You are no longer on the air.";

        private SpeechArbiter NewArbiter() => new SpeechArbiter(
            _clock,
            () => VerbosityLevel.Chatty,
            (message, interrupt, intent, level, origin, salvaged) =>
            {
                long t = ++_nextTicket;
                _calls.Add(new SinkCall(message, salvaged, t));
                return SpeechHandoff.TrackedAs(t);
            },
            () => { },
            (message, level, intent, origin) => { });

        private IEnumerable<string> Salvaged() => _calls.Where(c => c.Salvaged).Select(c => c.Message);
        private long TicketOf(string m) => _calls.Last(c => c.Message == m).Ticket;

        private void CutPartWayByNobodyWeCanName(SpeechArbiter a, string message, int at, int of, int ms)
            => a.OnOutcome(TicketOf(message), message, SpeechOutcome.Cancelled(at, of, byUs: false, ms));

        [Theory]
        [InlineData(true)]    // the operator keyed again before the replay: withdrawn
        [InlineData(false)]   // the positive control: same transmission episode, replayed as before
        public void AnUnheardCut_IsWithdrawnRatherThanReplayed_OnceTheOperatorHasKeyedAgain(bool keyedAgain)
        {
            // The producer's episode counter, as PttSafetyController keeps it:
            // advanced at every transmit start, captured when the cut speaks.
            int transmitEpisode = 1;
            int cutEpisode = transmitEpisode;
            var a = NewArbiter();
            a.Urgent(Cut, VerbosityLevel.Critical, "PttSafetyController", SpeechSubject.ReflectedPowerCut,
                stillValid: () => transmitEpisode == cutEpisode);
            CutPartWayByNobodyWeCanName(a, Cut, at: 3, of: 17, ms: 900);
            Assert.Contains(a.OwedSafetyObligations, o => o.Message == Cut);

            if (keyedAgain) transmitEpisode = 2;   // PttDown: a new transmission

            // The ordinary interrupt that used to rescue the copy, and its settle.
            _clock.Advance(1000);
            a.Emit("Slice A", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys", SpeechSubject.WhereYouAre);
            _clock.Advance(Settle);

            if (keyedAgain)
            {
                Assert.DoesNotContain(Cut, Salvaged());
                // Withdrawn, not merely paused: the owed list no longer claims
                // it, because nothing true is owed any more. The trace is the
                // record of what was not delivered.
                Assert.DoesNotContain(a.OwedSafetyObligations, o => o.Message == Cut);
            }
            else
            {
                Assert.Contains(Cut, Salvaged());
                Assert.Contains(a.OwedSafetyObligations, o => o.Message == Cut);
            }
        }

        [Fact]
        public void TheQuestionIsAskedAtEveryReplay_NotOnlyTheFirst()
        {
            // Still true at the first replay; keyed again before the second.
            int transmitEpisode = 1;
            int cutEpisode = transmitEpisode;
            var a = NewArbiter();
            a.Urgent(Cut, VerbosityLevel.Critical, "PttSafetyController", SpeechSubject.ReflectedPowerCut,
                stillValid: () => transmitEpisode == cutEpisode);
            CutPartWayByNobodyWeCanName(a, Cut, at: 3, of: 17, ms: 900);

            _clock.Advance(1000);
            a.Emit("Slice A", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys", SpeechSubject.WhereYouAre);
            _clock.Advance(Settle);
            Assert.Equal(1, Salvaged().Count(m => m == Cut));
            CutPartWayByNobodyWeCanName(a, Cut, at: 2, of: 17, ms: 600);

            transmitEpisode = 2;
            _clock.Advance(1000);
            a.Emit("Slice B", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys", SpeechSubject.WhereYouAre);
            _clock.Advance(Settle);
            Assert.Equal(1, Salvaged().Count(m => m == Cut));   // no second replay
            Assert.DoesNotContain(a.OwedSafetyObligations, o => o.Message == Cut);
        }

        [Fact]
        public void AProducerThatCannotAnswer_DoesNotGetItsPresentStateClaimReplayed()
        {
            // "Cannot say" is not permission to tell the operator a present
            // state. The sentence is not replayed, and the trace says why.
            var a = NewArbiter();
            a.Urgent(Cut, VerbosityLevel.Critical, "PttSafetyController", SpeechSubject.ReflectedPowerCut,
                stillValid: () => throw new InvalidOperationException("the controller is gone"));
            CutPartWayByNobodyWeCanName(a, Cut, at: 3, of: 17, ms: 900);
            _clock.Advance(1000);
            a.Emit("Slice A", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys", SpeechSubject.WhereYouAre);
            _clock.Advance(Settle);
            Assert.DoesNotContain(Cut, Salvaged());
        }

        [Fact]
        public void ASentenceWithNoValidityAnswer_IsReplayedUnderTheExistingRules()
        {
            // The contract is opt-in: a producer that declares nothing keeps
            // the behaviour every existing test pins.
            var a = NewArbiter();
            a.Urgent(Cut, VerbosityLevel.Critical, "PttSafetyController", SpeechSubject.ReflectedPowerCut);
            CutPartWayByNobodyWeCanName(a, Cut, at: 3, of: 17, ms: 900);
            _clock.Advance(1000);
            a.Emit("Slice A", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys", SpeechSubject.WhereYouAre);
            _clock.Advance(Settle);
            Assert.Contains(Cut, Salvaged());
        }
    }

    /// <summary>
    /// The producer's half, pinned by source because <c>PttSafetyController</c>
    /// is WPF and unreachable from here without a window: every transmit
    /// start advances the episode, and every safety outcome and warning that
    /// claims a present transmit state passes the answer.
    /// </summary>
    public class TransmitStateClaimValidityTests
    {
        private static string Ptt() =>
            File.ReadAllText(Path.Combine(RepoRoot(), "JJFlexWpf", "PttSafetyController.cs"));

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        [Fact]
        public void EveryTransmitStart_AdvancesTheEpisode()
        {
            string src = Ptt();
            // Positive control: the starts are where they were.
            int starts = System.Text.RegularExpressions.Regex.Matches(src, @"SetTx\(true\);").Count;
            Assert.Equal(2, starts);
            // Each one is immediately preceded by the episode advancing.
            Assert.Equal(starts, System.Text.RegularExpressions.Regex.Matches(src,
                @"_transmitEpisode\+\+;[^\n]*\r?\n\s*SetTx\(true\);").Count);
        }

        [Fact]
        public void TheSafetyOutcome_AndTheTransmitWarnings_CarryTheValidityAnswer()
        {
            string src = Ptt();
            // GoIdle's Urgent outcome: valid while no new transmission has begun.
            Assert.Contains("stillValid: forceSpeech ? () => _transmitEpisode == endedEpisode : null", src);
            // The reflected-power warning and the about-to-end warning: valid
            // while THIS transmission is still running.
            Assert.Contains("stillValid: () => State != PttState.Idle && _transmitEpisode == warnedEpisode", src);
            Assert.Contains("stillValid: () => State != PttState.Idle && _transmitEpisode == endingEpisode", src);
        }
    }
}
