#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    // ────────────────────────────────────────────────────────────────
    //  The paced delivery (#521): one in flight, our queue behind it, the
    //  interrupt path, and the three-stage escape from a call that will not
    //  return. Driven against FakeCompletionChannel so no reader is needed
    //  and nothing sounds. These use real threads and short deadlines; the
    //  waits are generous so a loaded machine cannot make them lie.
    // ────────────────────────────────────────────────────────────────
    public class PacedSpeechDeliveryTests : IDisposable
    {
        private readonly FakeCompletionChannel _ch = new();
        private readonly List<(long Ticket, string Text, SpeechOutcome Outcome)> _outcomes = new();
        private readonly List<(string Text, bool Interrupt)> _fallbacks = new();
        private readonly AutoResetEvent _reported = new(false);
        private PacedSpeechDelivery? _pump;

        private PacedSpeechDelivery NewPump(int deadlineMs = 400, int cancelGraceMs = 300, int escapeGraceMs = 300, int markGraceMs = 300)
        {
            _pump = new PacedSpeechDelivery(
                (text, interrupt) => { lock (_fallbacks) _fallbacks.Add((text, interrupt)); return SpeechDelivery.Accepted; },
                (ticket, text, outcome) => { lock (_outcomes) _outcomes.Add((ticket, text, outcome)); _reported.Set(); },
                _ => deadlineMs, cancelGraceMs, escapeGraceMs, markGraceMs);
            return _pump;
        }

        private (long Ticket, string Text, SpeechOutcome Outcome) WaitForOutcome(long ticket, int timeoutMs = 5000)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (true)
            {
                lock (_outcomes)
                {
                    var hit = _outcomes.FirstOrDefault(o => o.Ticket == ticket);
                    if (hit.Text != null) return hit;
                }
                int remaining = (int)(deadline - Environment.TickCount64);
                if (remaining <= 0) throw new TimeoutException($"no outcome for #{ticket}");
                _reported.WaitOne(remaining);
            }
        }

        public void Dispose()
        {
            _ch.ReleaseAll();
            _pump?.Dispose();
        }

        [Fact]
        public void OneInFlight_FifoBehindIt()
        {
            var p = NewPump();
            long a = p.Enqueue(_ch, "First sentence here.");
            long b = p.Enqueue(_ch, "Second sentence here.");

            var callA = _ch.WaitForCall();
            Assert.Equal(a, callA.Ticket);
            Thread.Sleep(50);
            Assert.Single(_ch.Seen);           // B has NOT been handed over
            Assert.Equal(1, p.QueuedCount);

            _ch.Complete(callA, 700);
            var oa = WaitForOutcome(a);
            Assert.Equal(SpeechOutcomeKind.Completed, oa.Outcome.Kind);
            Assert.Equal(700, oa.Outcome.ElapsedMs);

            var callB = _ch.WaitForCall();
            Assert.Equal(b, callB.Ticket);
            _ch.Complete(callB);
            Assert.Equal(SpeechOutcomeKind.Completed, WaitForOutcome(b).Outcome.Kind);
            Assert.Equal(new[] { a, b }, _ch.Seen.Select(s => s.Ticket).ToArray());
        }

        [Fact]
        public void Interrupt_CancelsInFlight_WithdrawsTheQueue_GoesNext()
        {
            var p = NewPump();
            long a = p.Enqueue(_ch, "The one in flight.");
            long b = p.Enqueue(_ch, "The one waiting.");
            var callA = _ch.WaitForCall();

            long c = p.Interrupt(_ch, "The interrupter.");

            // A came back cancelled BY US; B never reached the reader and
            // has no outcome (the arbiter's ledger holds it); C is next.
            var oa = WaitForOutcome(a);
            Assert.Equal(SpeechOutcomeKind.Cancelled, oa.Outcome.Kind);
            Assert.True(oa.Outcome.CancelledByUs);
            Assert.True(_ch.CancelCalls >= 1);

            var callC = _ch.WaitForCall();
            Assert.Equal(c, callC.Ticket);
            _ch.Complete(callC);
            WaitForOutcome(c);

            Assert.DoesNotContain(_ch.Seen, s => s.Ticket == b);
            lock (_outcomes) Assert.DoesNotContain(_outcomes, o => o.Ticket == b);
            Assert.Equal(0, p.QueuedCount);
        }

        [Fact]
        public void Interrupt_WithNothingInFlight_StillCutsTheReader()
        {
            var p = NewPump();
            long c = p.Interrupt(_ch, "Now.");
            Assert.Equal(1, _ch.CancelCalls);   // the reader's own speech is cut, as it always was
            var call = _ch.WaitForCall();
            Assert.Equal(c, call.Ticket);
            _ch.Complete(call);
            WaitForOutcome(c);
        }

        [Fact]
        public void Discard_WithdrawsEverything_AndTheInFlightAnswerIsStillReported()
        {
            var p = NewPump();
            long a = p.Enqueue(_ch, "Speaking now.");
            p.Enqueue(_ch, "Waiting.");
            _ch.WaitForCall();

            p.Discard("test silence");
            var oa = WaitForOutcome(a);
            Assert.Equal(SpeechOutcomeKind.Cancelled, oa.Outcome.Kind);
            Assert.True(oa.Outcome.CancelledByUs);
            Assert.Equal(0, p.QueuedCount);
            Thread.Sleep(50);
            Assert.Single(_ch.Seen);
        }

        [Fact]
        public void ForeignCancel_IsReportedAsNotOurs_AndWithdrawsTheWholeQueue()
        {
            // #562, ruled by Noel 2026-09-07: "ctrl always means silence when
            // it comes to NVDA's shut up key."
            //
            // This test asserted the opposite until that ruling — that the
            // keystroke destroyed one utterance and the backlog carried on.
            // What that produced at the radio was Ctrl silencing the sentence
            // in flight and the app immediately starting the next one, which
            // is not what the shut-up key means anywhere else in Windows.
            //
            // We cannot tell Ctrl from any other key: both come back 1223 with
            // CancelledByUs false. We do not try. "I pressed a key, it stopped,
            // then it started talking again" is one complaint, not two.
            var p = NewPump();
            long a = p.Enqueue(_ch, "Connected to FLEX-8600, SmartLink, 4 slices.");
            long b = p.Enqueue(_ch, "PC audio on.");
            var callA = _ch.WaitForCall();

            _ch.Mark(callA); _ch.Mark(callA);
            _ch.CancelFromOutside(callA, marksReached: 2);

            var oa = WaitForOutcome(a);
            Assert.Equal(SpeechOutcomeKind.Cancelled, oa.Outcome.Kind);
            Assert.False(oa.Outcome.CancelledByUs);
            Assert.Equal(2, oa.Outcome.MarksReached);
            Assert.Equal(6, oa.Outcome.MarkCount);

            // B is WITHDRAWN, not spoken. The channel never sees it, and the
            // pump holds nothing — the arbiter's ledger still has it and #503's
            // subject rules decide whether it earns another hearing.
            Assert.Throws<TimeoutException>(() => _ch.WaitForCall(400));
            Assert.Equal(0, p.QueuedCount);
            Assert.False(p.InFlight);
            Assert.DoesNotContain(_ch.Seen, c => c.Ticket == b);
        }

        [Fact]
        public void Completion_Does_NOT_WithdrawTheQueue()
        {
            // The negative control #562 needs, and NOT the one I first wrote.
            // My first attempt asserted that our OWN cancel keeps the queue —
            // it does not, and never did: Interrupt withdraws by design, so
            // that test was asserting something false and failed immediately.
            //
            // The real must-not-fire case is a clean completion. If the new
            // rule were written a shade too wide, the queue would drain after
            // every successful utterance and only the first thing said in any
            // batch would ever be heard — which is the connect briefing broken
            // in a way no single-utterance test would notice.
            var p = NewPump();
            long a = p.Enqueue(_ch, "Connected to FLEX-8600, SmartLink, 4 slices.");
            long b = p.Enqueue(_ch, "PC audio on.");

            var callA = _ch.WaitForCall();
            _ch.Complete(callA);
            var oa = WaitForOutcome(a);
            Assert.Equal(SpeechOutcomeKind.Completed, oa.Outcome.Kind);

            var callB = _ch.WaitForCall();
            Assert.Equal(b, callB.Ticket);
            _ch.Complete(callB);
            Assert.Equal(SpeechOutcomeKind.Completed, WaitForOutcome(b).Outcome.Kind);
        }

        [Fact]
        public void InvalidSsml_IsReSentThroughThePlainPath_AndReportedUnknown()
        {
            var p = NewPump();
            long a = p.Enqueue(_ch, "S 3 < 5");
            var call = _ch.WaitForCall();
            _ch.Refuse(call, SpeechUnknownReason.InvalidSsml);

            var oa = WaitForOutcome(a);
            Assert.Equal(SpeechOutcomeKind.Unknown, oa.Outcome.Kind);
            Assert.Equal(SpeechUnknownReason.InvalidSsml, oa.Outcome.UnknownReason);
            Assert.Contains("plain path", oa.Outcome.Detail);
            lock (_fallbacks) Assert.Equal(new[] { ("S 3 < 5", false) }, _fallbacks.ToArray());
        }

        [Fact]
        public void AccessDenied_IsNotRetried()
        {
            var p = NewPump();
            long a = p.Enqueue(_ch, "Refused text.");
            _ch.Refuse(_ch.WaitForCall(), SpeechUnknownReason.Refused);

            var oa = WaitForOutcome(a);
            Assert.Equal(SpeechUnknownReason.Refused, oa.Outcome.UnknownReason);
            lock (_fallbacks) Assert.Empty(_fallbacks);
        }

        [Fact]
        public void ChannelOff_DrainsThroughThePlainPath_ReportedChannelAbsent()
        {
            var p = NewPump();
            _ch.CanReportCompletion = false;
            long a = p.Enqueue(_ch, "Words that still matter.");
            var oa = WaitForOutcome(a);
            Assert.Equal(SpeechUnknownReason.ChannelAbsent, oa.Outcome.UnknownReason);
            lock (_fallbacks) Assert.Single(_fallbacks);
            Assert.Empty(_ch.Seen);
        }

        // ── The escape ──

        [Fact]
        public void Deadline_WithNoMarks_EscapesTheCall_ReportedUnknownEscaped()
        {
            var p = NewPump(deadlineMs: 300);
            _ch.EscapeWorks = true;
            long a = p.Enqueue(_ch, "This call will hang without progress.");
            _ch.WaitForCall();
            // Nothing completes it. The deadline passes, the RPC cancel is
            // asked for, the fake honours it.
            var oa = WaitForOutcome(a);
            Assert.Equal(SpeechOutcomeKind.Unknown, oa.Outcome.Kind);
            Assert.Equal(SpeechUnknownReason.Escaped, oa.Outcome.UnknownReason);
            Assert.Equal(1, _ch.EscapeAttempts);
            Assert.True(_ch.CanReportCompletion, "one escape is a warning, not a shutdown");
            Assert.Equal(0, p.AbandonedThreads);
        }

        [Fact]
        public void SecondEscape_TurnsTheChannelOff()
        {
            var p = NewPump(deadlineMs: 300);
            long a = p.Enqueue(_ch, "First hang.");
            _ch.WaitForCall();
            WaitForOutcome(a);
            long b = p.Enqueue(_ch, "Second hang.");
            _ch.WaitForCall();
            WaitForOutcome(b);
            // The worker reports the outcome; the timer thread that ran the
            // escape turns the channel off a moment later. Give it that moment.
            var until = Environment.TickCount64 + 2000;
            while (_ch.CanReportCompletion && Environment.TickCount64 < until) Thread.Sleep(10);
            Assert.False(_ch.CanReportCompletion);
            Assert.Contains("twice", _ch.DisabledReason);
        }

        [Fact]
        public void Marks_ExtendTheDeadline_ARespondingReaderIsNotHung()
        {
            var p = NewPump(deadlineMs: 250, markGraceMs: 400);
            long a = p.Enqueue(_ch, "One two three four five six seven eight nine ten.");
            var call = _ch.WaitForCall();

            // Marks every 150 ms for well over the 250 ms deadline.
            for (int i = 0; i < 8; i++) { Thread.Sleep(150); _ch.Mark(call); }
            _ch.Complete(call, 1300);

            var oa = WaitForOutcome(a);
            Assert.Equal(SpeechOutcomeKind.Completed, oa.Outcome.Kind);
            Assert.Equal(0, _ch.EscapeAttempts);
        }

        [Fact]
        public void EscapeRefused_AbandonsTheThread_ReportsTimeout_TurnsTheChannelOff_AndCarriesOn()
        {
            var p = NewPump(deadlineMs: 300, escapeGraceMs: 200);
            _ch.EscapeWorks = false;
            long a = p.Enqueue(_ch, "This call hangs and cannot be freed.");
            long b = p.Enqueue(_ch, "This one is still owed.");
            var callA = _ch.WaitForCall();

            var oa = WaitForOutcome(a);
            Assert.Equal(SpeechOutcomeKind.Unknown, oa.Outcome.Kind);
            Assert.Equal(SpeechUnknownReason.Timeout, oa.Outcome.UnknownReason);
            Assert.False(oa.Outcome.WasHeard);
            Assert.Equal(1, p.AbandonedThreads);
            Assert.False(_ch.CanReportCompletion);
            Assert.Contains("abandoned", _ch.DisabledReason);

            // A fresh worker drains B through the plain path, because the
            // channel is now off.
            var ob = WaitForOutcome(b);
            Assert.Equal(SpeechUnknownReason.ChannelAbsent, ob.Outcome.UnknownReason);
            lock (_fallbacks) Assert.Contains(_fallbacks, f => f.Text == "This one is still owed.");

            // The abandoned thread eventually returning must NOT produce a
            // second, contradicting outcome for A.
            _ch.Complete(callA);
            Thread.Sleep(200);
            lock (_outcomes) Assert.Single(_outcomes, o => o.Ticket == a);
        }

        [Fact]
        public void OurCancelUnanswered_IsAHang_AndEscapesInsideTheCancelGrace()
        {
            // The 2024.1 to 2026.1 shape: our cancelSpeech reaches NVDA, but
            // the blocked speakSsml never hears back. The interrupt goes
            // through a view of the channel whose Cancel() releases nothing,
            // so the only thing that can free the call is the RPC escape —
            // and it must fire inside the short cancel grace, not the long
            // utterance deadline.
            var p = NewPump(deadlineMs: 5000, cancelGraceMs: 200, escapeGraceMs: 200);
            long a = p.Enqueue(_ch, "A call that ignores our cancel.");
            _ch.WaitForCall();
            _ch.EscapeWorks = true;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            p.Interrupt(new NonReleasingChannel(_ch), "Interrupter.");
            var oa = WaitForOutcome(a);
            sw.Stop();

            Assert.Equal(SpeechUnknownReason.Escaped, oa.Outcome.UnknownReason);
            Assert.True(sw.ElapsedMilliseconds < 3000, $"took {sw.ElapsedMilliseconds} ms, the 5 s deadline was not the escape");
        }

        /// <summary>A view of the fake whose Cancel() reaches the reader but does not release the blocked call — the 2024.1 to 2026.1 hang.</summary>
        private sealed class NonReleasingChannel : ISpeechCompletionChannel
        {
            private readonly FakeCompletionChannel _inner;
            public NonReleasingChannel(FakeCompletionChannel inner) { _inner = inner; }
            public bool CanReportCompletion => _inner.CanReportCompletion;
            public string Name => _inner.Name;
            public SpeechOutcome SpeakAndWait(string text, long ticket, Action<int>? onMarkReached) => _inner.SpeakAndWait(text, ticket, onMarkReached);
            public void Cancel() { /* reaches NVDA; the blocked call never hears back */ }
            public bool TryCancelBlockedCall() => _inner.TryCancelBlockedCall();
            public void Disable(string reason) => _inner.Disable(reason);
        }

        [Fact]
        public void DefaultDeadline_IsBounded_AndScalesWithTheUtterance()
        {
            int shortMs = PacedSpeechDelivery.DefaultDeadlineMs("Yes.");
            int longMs = PacedSpeechDelivery.DefaultDeadlineMs(string.Join(" ", Enumerable.Repeat("word", 200)));
            Assert.Equal(PacedSpeechDelivery.DeadlineFloorMs, shortMs);
            // The ledger's own estimate caps at SalvageCapMs, so the longest
            // deadline any utterance can earn is twice that plus the slack;
            // DeadlineCapMs above it is a defensive bound, not the working one.
            Assert.Equal(2 * SpeechArbiter.SalvageCapMs + 3000, longMs);
            Assert.True(longMs <= PacedSpeechDelivery.DeadlineCapMs);
            int batch = PacedSpeechDelivery.DefaultDeadlineMs("Connected to FLEX-8600, SmartLink, 4 slices. This radio had no mic profile, so I loaded Default. PC audio off. Recording is on. JJ Flexible Home, Modern tuning mode");
            Assert.InRange(batch, 15000, 25000);   // measured 8079 ms; twice the ~8.8 s estimate plus slack
        }
    }
}
