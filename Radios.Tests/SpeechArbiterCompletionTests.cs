#nullable enable
using System.Collections.Generic;
using System.Linq;
using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    // ────────────────────────────────────────────────────────────────
    //  The ledger with a reader that can answer (#521).
    //
    //  The sink here hands out a TICKET per call, the way the paced delivery
    //  does, and the tests then play the reader's answers back through
    //  OnOutcome. Every assertion is about what the NEXT interrupt salvages
    //  — the only observable the ledger has — driven on the fake clock so
    //  "the estimate would have pruned this by now" is a fact, not a sleep.
    // ────────────────────────────────────────────────────────────────
    public class SpeechArbiterCompletionTests
    {
        private sealed record SinkCall(string Message, bool Interrupt, bool Salvaged, long Ticket, double AtMs);

        private readonly FakeSpeechClock _clock = new();
        private readonly List<SinkCall> _calls = new();
        private long _nextTicket;
        private bool _ticketed = true;
        private bool? _isSpeaking;
        private SpeechRateModel _rate = new();

        private const int Settle = SpeechArbiter.SalvageSettleMs;

        private const string Lead = "Connected to FLEX-8600, SmartLink, 4 slices.";
        private const string Mic = "This radio had no mic profile, so I loaded Default.";
        private const string Pc = "PC audio on.";
        private const string Bye = "Disconnecting from K5NER, goodbye";

        private SpeechArbiter NewArbiter() => new SpeechArbiter(
            _clock,
            () => VerbosityLevel.Chatty,
            (message, interrupt, intent, level, origin, salvaged) =>
            {
                long ticket = _ticketed ? ++_nextTicket : 0;
                _calls.Add(new SinkCall(message, interrupt, salvaged, ticket, _clock.ElapsedMs));
                return _ticketed ? SpeechHandoff.TrackedAs(ticket) : SpeechHandoff.Untracked;
            },
            () => { },
            (message, level, intent, origin) => { },
            _rate,
            () => _isSpeaking);

        private IEnumerable<string> Salvaged() => _calls.Where(c => c.Salvaged).Select(c => c.Message);
        private long TicketOf(string message) => _calls.Last(c => c.Message == message && !c.Salvaged).Ticket;

        // ── The #521 case, as a test ──

        [Fact]
        public void TheDisconnectCase_HeardUtterancesAreNotRescued()
        {
            // 17433: five queued in one millisecond. 27871: the operator
            // disconnects, the landing prefix interrupts. Before #521 the
            // arbiter rescued four of them and told him he was connected.
            var a = NewArbiter();
            a.Emit(Lead, false, SpeechIntent.Queue, VerbosityLevel.Terse, "t", "connect-lead");
            a.Emit(Mic, false, SpeechIntent.Queue, VerbosityLevel.Terse, "t", "mic-profile");
            a.Emit(Pc, false, SpeechIntent.Queue, VerbosityLevel.Terse, "t", "pc-audio");

            // The reader worked through the first two and was on the third
            // when the interrupt came.
            _clock.Advance(3500);
            a.OnOutcome(TicketOf(Lead), Lead, SpeechOutcome.Completed(6, 6, 3400));
            _clock.Advance(4000);
            a.OnOutcome(TicketOf(Mic), Mic, SpeechOutcome.Completed(10, 10, 3900));
            _clock.Advance(3000);

            a.Emit(Bye, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t", "where-you-are");
            a.OnOutcome(TicketOf(Pc), Pc, SpeechOutcome.Cancelled(1, 3, byUs: true, 300));
            _clock.Advance(Settle);

            // Only the one that was actually cut comes back. The two the
            // operator heard are gone from the ledger, and the false
            // "Connected" is never said again.
            Assert.Equal(new[] { Pc }, Salvaged().ToArray());
        }

        // ── Tracked entries are governed by the reader, not the clock ──

        [Fact]
        public void TrackedEntry_IsNotPrunedByTheEstimate()
        {
            var a = NewArbiter();
            a.Emit(Pc, false, SpeechIntent.Queue, VerbosityLevel.Terse, "t", "pc-audio");

            // Well past the estimate for a three-word sentence, but under
            // the 15 s ceiling. An untracked entry would be gone by now.
            _clock.Advance(SpeechArbiter.EstimateSpokenMs(Pc) * 3);
            a.Emit(Bye, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t", "where-you-are");
            _clock.Advance(Settle);

            Assert.Equal(new[] { Pc }, Salvaged().ToArray());
        }

        [Fact]
        public void UntrackedEntry_StillPrunedByTheEstimate_ExactlyAsBefore()
        {
            _ticketed = false;
            var a = NewArbiter();
            a.Emit(Pc, false, SpeechIntent.Queue, VerbosityLevel.Terse, "t", "pc-audio");

            _clock.Advance(SpeechArbiter.EstimateSpokenMs(Pc) + 1);
            a.Emit(Bye, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t", "where-you-are");
            _clock.Advance(Settle);

            Assert.Empty(Salvaged());
        }

        [Fact]
        public void TrackedEntry_DoesNotPushTheBusyUntil_ForUntrackedOnesBehindIt()
        {
            // A long tracked lead, then a short untracked fact. The fact's
            // estimate must start NOW, not after the lead's — the lead is on
            // the reader's clock, not the estimate's.
            var a = NewArbiter();
            a.Emit(Lead, false, SpeechIntent.Queue, VerbosityLevel.Terse, "t", "connect-lead");
            _ticketed = false;
            a.Emit(Pc, false, SpeechIntent.Queue, VerbosityLevel.Terse, "t", "pc-audio");

            _clock.Advance(SpeechArbiter.EstimateSpokenMs(Pc) + 1);
            _ticketed = true;
            a.Emit(Bye, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t", "where-you-are");
            _clock.Advance(Settle);

            // Pc pruned on its own estimate; Lead still tracked and rescued.
            Assert.Equal(new[] { Lead }, Salvaged().ToArray());
        }

        // ── Each outcome kind ──

        [Fact]
        public void Completed_RemovesTheEntry_WhereverItIs()
        {
            var a = NewArbiter();
            a.Emit(Lead, false, SpeechIntent.Queue, VerbosityLevel.Terse, "t", "connect-lead");
            a.Emit(Pc, false, SpeechIntent.Queue, VerbosityLevel.Terse, "t", "pc-audio");

            // Interrupt first, so both are in the HELD set, then the reader
            // says the lead had in fact finished (it raced our cancel).
            a.Emit(Bye, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t", "where-you-are");
            a.OnOutcome(TicketOf(Lead), Lead, SpeechOutcome.Completed(6, 6, 3300));
            _clock.Advance(Settle);

            Assert.Equal(new[] { Pc }, Salvaged().ToArray());
        }

        [Fact]
        public void CancelledNotByUs_StaysForTheNextInterruptToJudge_AndIsNotReSpokenOnItsOwn()
        {
            var a = NewArbiter();
            a.Emit(Lead, false, SpeechIntent.Queue, VerbosityLevel.Terse, "t", "connect-lead");

            // The operator's own keystroke cut it at word two.
            a.OnOutcome(TicketOf(Lead), Lead, SpeechOutcome.Cancelled(2, 6, byUs: false, 900));
            _clock.Advance(Settle * 3);

            // No rescue happens on the cancel itself — that is #554's runaway.
            Assert.Empty(Salvaged());

            // The next interrupt judges it under the ordinary rules and,
            // nothing having covered its subject, rescues it.
            a.Emit(Bye, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t", "where-you-are");
            _clock.Advance(Settle);
            Assert.Equal(new[] { Lead }, Salvaged().ToArray());
        }

        [Fact]
        public void CancelledEntry_IsStillRetiredBySupersession()
        {
            var a = NewArbiter();
            a.Emit(Pc, false, SpeechIntent.Queue, VerbosityLevel.Terse, "t", "pc-audio");
            a.OnOutcome(TicketOf(Pc), Pc, SpeechOutcome.Cancelled(1, 3, byUs: false, 200));

            // Something newer on the same subject.
            a.Emit("PC audio off.", false, SpeechIntent.Queue, VerbosityLevel.Terse, "t", "pc-audio");
            a.Emit(Bye, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t", "where-you-are");
            _clock.Advance(Settle);

            Assert.Equal(new[] { "PC audio off." }, Salvaged().ToArray());
        }

        [Fact]
        public void UnknownTimeout_FallsBackToTheEstimate_FromNow()
        {
            var a = NewArbiter();
            a.Emit(Pc, false, SpeechIntent.Queue, VerbosityLevel.Terse, "t", "pc-audio");
            _clock.Advance(5000);
            a.OnOutcome(TicketOf(Pc), Pc, SpeechOutcome.Unknown(SpeechUnknownReason.Timeout, "hung", 5000));

            // Inside the estimate from the moment of the answer: still protected.
            _clock.Advance(SpeechArbiter.EstimateSpokenMs(Pc) - 1);
            a.Emit(Bye, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t", "where-you-are");
            _clock.Advance(Settle);
            Assert.Equal(new[] { Pc }, Salvaged().ToArray());
        }

        [Fact]
        public void UnknownTimeout_ThenPastTheEstimate_IsPruned()
        {
            var a = NewArbiter();
            a.Emit(Pc, false, SpeechIntent.Queue, VerbosityLevel.Terse, "t", "pc-audio");
            a.OnOutcome(TicketOf(Pc), Pc, SpeechOutcome.Unknown(SpeechUnknownReason.Timeout, "hung", 9000));

            _clock.Advance(SpeechArbiter.EstimateSpokenMs(Pc) + 1);
            a.Emit(Bye, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t", "where-you-are");
            _clock.Advance(Settle);
            Assert.Empty(Salvaged());
        }

        [Fact]
        public void UnknownRefused_LeavesTheLedger_NothingIsOwed()
        {
            var a = NewArbiter();
            a.Emit(Pc, false, SpeechIntent.Queue, VerbosityLevel.Terse, "t", "pc-audio");
            a.OnOutcome(TicketOf(Pc), Pc, SpeechOutcome.Unknown(SpeechUnknownReason.Refused, "asleep", 5));

            a.Emit(Bye, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t", "where-you-are");
            _clock.Advance(Settle);
            Assert.Empty(Salvaged());
        }

        [Fact]
        public void OutcomeForAnUnknownTicket_DoesNothing_AndDoesNotThrow()
        {
            var a = NewArbiter();
            a.Emit(Pc, false, SpeechIntent.Queue, VerbosityLevel.Terse, "t", "pc-audio");
            a.OnOutcome(999, "never sent", SpeechOutcome.Completed(1, 1, 10));
            a.OnOutcome(0, Pc, SpeechOutcome.Completed(3, 3, 10));   // ticket 0 is "untracked", never matches

            a.Emit(Bye, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t", "where-you-are");
            _clock.Advance(Settle);
            Assert.Equal(new[] { Pc }, Salvaged().ToArray());
        }

        // ── Re-handed salvages get a fresh ticket ──

        [Fact]
        public void ASalvage_IsANewQuestion_WithANewTicket()
        {
            var a = NewArbiter();
            a.Emit(Pc, false, SpeechIntent.Queue, VerbosityLevel.Terse, "t", "pc-audio");
            long first = TicketOf(Pc);

            a.Emit(Bye, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t", "where-you-are");
            a.OnOutcome(first, Pc, SpeechOutcome.Cancelled(1, 3, byUs: true, 200));
            _clock.Advance(Settle);

            var rehanded = _calls.Single(c => c.Salvaged);
            Assert.NotEqual(first, rehanded.Ticket);

            // The OLD ticket no longer names it; the NEW one does.
            a.OnOutcome(first, Pc, SpeechOutcome.Completed(3, 3, 900));
            a.Emit("Slice A", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t", "where-you-are");
            _clock.Advance(Settle);
            Assert.Equal(2, _calls.Count(c => c.Salvaged));   // rescued again: the old ticket did not retire it

            a.OnOutcome(_calls.Last(c => c.Salvaged).Ticket, Pc, SpeechOutcome.Completed(3, 3, 900));
            a.Emit("Slice B", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t", "where-you-are");
            _clock.Advance(Settle);
            Assert.Equal(2, _calls.Count(c => c.Salvaged));   // heard; not rescued a third time
        }

        // ── The estimate correction (#557) ──

        [Fact]
        public void IsSpeakingFalse_RetiresEstimatedEntriesEarly()
        {
            _ticketed = false;
            _isSpeaking = false;
            var a = NewArbiter();
            a.Emit(Lead, false, SpeechIntent.Queue, VerbosityLevel.Terse, "t", "connect-lead");

            // One millisecond later, well inside the lead's estimate, the
            // backend says it is not speaking. The estimate was wrong in the
            // direction that keeps stale entries alive; the entry goes.
            _clock.Advance(1);
            a.Emit(Bye, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t", "where-you-are");
            _clock.Advance(Settle);
            Assert.Empty(Salvaged());
        }

        [Fact]
        public void IsSpeakingTrueOrUnknown_LeavesTheEstimateAlone()
        {
            foreach (bool? answer in new bool?[] { true, null })
            {
                _calls.Clear();
                _ticketed = false;
                _isSpeaking = answer;
                var a = NewArbiter();
                a.Emit(Lead, false, SpeechIntent.Queue, VerbosityLevel.Terse, "t", "connect-lead");
                _clock.Advance(1);
                a.Emit(Bye, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t", "where-you-are");
                _clock.Advance(Settle);
                Assert.Equal(new[] { Lead }, Salvaged().ToArray());
            }
        }

        [Fact]
        public void IsSpeaking_IsNeverAskedAboutTrackedEntries()
        {
            int probes = 0;
            _isSpeaking = false;
            var a = new SpeechArbiter(
                _clock, () => VerbosityLevel.Chatty,
                (m, i, _, _, _, s) => { _calls.Add(new SinkCall(m, i, s, ++_nextTicket, _clock.ElapsedMs)); return SpeechHandoff.TrackedAs(_nextTicket); },
                () => { }, (_, _, _, _) => { }, _rate,
                () => { probes++; return false; });

            a.Emit(Lead, false, SpeechIntent.Queue, VerbosityLevel.Terse, "t", "connect-lead");
            a.Emit(Bye, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t", "where-you-are");
            _clock.Advance(Settle);

            Assert.Equal(0, probes);
            Assert.Equal(new[] { Lead }, Salvaged().ToArray());
        }

        [Fact]
        public void CompletedOutcomes_TeachTheRateModel_EvenForInterrupters()
        {
            var a = NewArbiter();
            a.Emit(Lead, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t", "connect-lead");
            long ticket = _calls.Single().Ticket;
            Assert.Equal(1.0, _rate.Scale);

            // Twice as slow as the reference rate, reported for an
            // interrupter that was never in the ledger.
            a.OnOutcome(ticket, Lead, SpeechOutcome.Completed(6, 6, SpeechRateModel.Uncalibrated(Lead) * 2));
            Assert.True(_rate.Scale > 1.0);
            Assert.Equal(1, _rate.Samples);
        }

        [Fact]
        public void ABoolSink_IsAnUntrackedHandoff()
        {
            SpeechHandoff h = true;
            Assert.True(h.Reached);
            Assert.False(h.Tracked);
            Assert.Equal(0, h.Ticket);
            SpeechHandoff no = false;
            Assert.False(no.Reached);
        }
    }
}
