using System;
using System.Collections.Generic;
using System.Linq;
using Radios;
using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    // ────────────────────────────────────────────────────────────────
    //  Deterministic tests for the speech arbiter (Sprint 35 Track L).
    //
    //  The coalescer's behaviour IS its timing, and until the clock seam
    //  existed none of it was testable without sleeping — slow, flaky,
    //  and an instrument that lies occasionally. Every test here drives a
    //  manual clock by hand: advance to 1199 ms and assert silence,
    //  advance one more and assert exactly one utterance. The three
    //  constants (CoalesceMs 300, SweepWindowMs 1200, MinGapMs 1200) are
    //  exercised at their exact boundaries.
    //
    //  The arbiter is an instance class with injected callbacks, so these
    //  tests touch NO process-wide statics — no collection serialisation
    //  needed, safe to run in parallel with everything else.
    // ────────────────────────────────────────────────────────────────
    public class SpeechArbiterTests
    {
        // ── Test doubles ──

        private sealed class FakeSpeechClock : ISpeechClock
        {
            private DateTime _now = new DateTime(2026, 8, 26, 0, 0, 0, DateTimeKind.Utc);
            private readonly List<FakeTimer> _timers = new();
            private long _seq;

            public DateTime UtcNow => _now;
            public DateTime Epoch { get; } = new DateTime(2026, 8, 26, 0, 0, 0, DateTimeKind.Utc);

            public ISpeechTimer StartTimer(int dueMs, Action callback)
            {
                var t = new FakeTimer(this, callback, _now.AddMilliseconds(dueMs), _seq++);
                _timers.Add(t);
                return t;
            }

            /// <summary>
            /// Move time forward, firing due timers in due order. A callback
            /// that re-arms its own timer (the MinGap deferral path does) is
            /// honoured within the same advance when the new due time still
            /// falls inside it — exactly as a real timer would fire.
            /// </summary>
            public void Advance(int ms)
            {
                var target = _now.AddMilliseconds(ms);
                while (true)
                {
                    FakeTimer? next = _timers
                        .Where(t => !t.Disposed && t.DueAt.HasValue && t.DueAt.Value <= target)
                        .OrderBy(t => t.DueAt!.Value).ThenBy(t => t.Seq)
                        .FirstOrDefault();
                    if (next == null) break;
                    if (next.DueAt!.Value > _now) _now = next.DueAt.Value;
                    next.DueAt = null; // one-shot: consumed unless re-armed
                    next.Callback();
                }
                _now = target;
            }

            private sealed class FakeTimer : ISpeechTimer
            {
                private readonly FakeSpeechClock _clock;
                public Action Callback { get; }
                public DateTime? DueAt { get; set; }
                public long Seq { get; }
                public bool Disposed { get; private set; }

                public FakeTimer(FakeSpeechClock clock, Action callback, DateTime dueAt, long seq)
                {
                    _clock = clock;
                    Callback = callback;
                    DueAt = dueAt;
                    Seq = seq;
                }

                public void Change(int dueMs)
                {
                    // Matches System.Threading.Timer: a disposed timer's Change
                    // throws, and the arbiter's race handling keys on that.
                    if (Disposed) throw new ObjectDisposedException(nameof(FakeTimer));
                    DueAt = _clock._now.AddMilliseconds(dueMs);
                }

                public void Dispose()
                {
                    Disposed = true;
                    DueAt = null;
                }
            }
        }

        private sealed record SinkCall(
            string Message, bool Interrupt, SpeechIntent? Intent,
            VerbosityLevel? Level, string? Origin, bool Salvaged, double AtMs);

        private readonly FakeSpeechClock _clock = new();
        private readonly List<SinkCall> _calls = new();
        private readonly List<(string Message, VerbosityLevel Level)> _gated = new();
        private int _silenceCount;
        private VerbosityLevel _verbosity = VerbosityLevel.Chatty;
        private bool _sinkResult = true;

        private SpeechArbiter NewArbiter() => new SpeechArbiter(
            _clock,
            () => _verbosity,
            (message, interrupt, intent, level, origin, salvaged) =>
            {
                _calls.Add(new SinkCall(message, interrupt, intent, level, origin, salvaged,
                    (_clock.UtcNow - _clock.Epoch).TotalMilliseconds));
                return _sinkResult;
            },
            () => _silenceCount++,
            (message, level, intent, origin) => _gated.Add((message, level)));

        // ── Latest: lead-then-settle, at exact boundaries ──

        [Fact]
        public void Latest_FirstPress_SpeaksImmediately()
        {
            var a = NewArbiter();

            a.Latest("rf", "RF gain 5", VerbosityLevel.Terse, false, "t");

            var call = Assert.Single(_calls);
            Assert.Equal("RF gain 5", call.Message);
            Assert.True(call.Interrupt);
            Assert.Equal(0, call.AtMs);

            // A single deliberate press produces exactly one utterance, ever.
            _clock.Advance(10_000);
            Assert.Single(_calls);
        }

        [Fact]
        public void Latest_SecondValueDuringSweep_SettlesExactlyAtMinGap()
        {
            var a = NewArbiter();
            a.Latest("rf", "RF gain 5", VerbosityLevel.Terse, false, "t");   // lead at 0

            _clock.Advance(400);
            a.Latest("rf", "RF gain 6", VerbosityLevel.Terse, false, "t");   // coalesces

            // CoalesceMs would flush at 700, but MinGapMs (1200 from the lead)
            // defers the settle so it cannot clip the lead's tail. Nothing may
            // speak at 700, nothing at 1199 — and at exactly 1200 the settle
            // fires once with the newest value.
            _clock.Advance(299);            // t = 699
            Assert.Single(_calls);
            _clock.Advance(1);              // t = 700 — coalesce due, gap defers
            Assert.Single(_calls);
            _clock.Advance(499);            // t = 1199
            Assert.Single(_calls);
            _clock.Advance(1);              // t = 1200
            Assert.Equal(2, _calls.Count);
            Assert.Equal("RF gain 6", _calls[1].Message);
            Assert.Equal(1200, _calls[1].AtMs);
            Assert.True(_calls[1].Interrupt);
        }

        [Fact]
        public void Latest_EveryNewValueRestartsTheCoalesceTimer()
        {
            var a = NewArbiter();
            a.Latest("tx", "TX Power 5", VerbosityLevel.Terse, false, "t");  // lead at 0

            // A debounce, not a throttle: each new value pushes the flush out
            // by CoalesceMs, so the sweep speaks once when the operator stops.
            _clock.Advance(400);
            a.Latest("tx", "TX Power 6", VerbosityLevel.Terse, false, "t");  // due 700
            _clock.Advance(200);
            a.Latest("tx", "TX Power 7", VerbosityLevel.Terse, false, "t");  // due 900
            _clock.Advance(200);
            a.Latest("tx", "TX Power 8", VerbosityLevel.Terse, false, "t");  // due 1100

            // Flush at 1100 is still inside MinGap (100 ms short), so the
            // settle waits out the remainder and lands at exactly 1200.
            _clock.Advance(399);            // t = 1199
            Assert.Single(_calls);
            _clock.Advance(1);              // t = 1200
            Assert.Equal(2, _calls.Count);
            Assert.Equal("TX Power 8", _calls[1].Message);
            Assert.Equal(1200, _calls[1].AtMs);

            // Intermediate values 6 and 7 were superseded and never spoken.
            Assert.DoesNotContain(_calls, c => c.Message is "TX Power 6" or "TX Power 7");
        }

        [Fact]
        public void Latest_SettleWithUnchangedValue_IsDropped()
        {
            var a = NewArbiter();
            a.Latest("rf", "RF gain 5", VerbosityLevel.Terse, false, "t");
            _clock.Advance(400);
            a.Latest("rf", "RF gain 5", VerbosityLevel.Terse, false, "t");   // same text

            // The settle would only repeat what the lead already said.
            _clock.Advance(10_000);
            Assert.Single(_calls);
        }

        [Fact]
        public void Latest_AfterSweepWindowExpires_LeadsAgainImmediately()
        {
            var a = NewArbiter();
            a.Latest("rf", "RF gain 5", VerbosityLevel.Terse, false, "t");

            // 1300 ms later the key is no longer sweeping (SweepWindowMs 1200)
            // and MinGap has elapsed: a deliberate press answers instantly.
            _clock.Advance(1300);
            a.Latest("rf", "RF gain 6", VerbosityLevel.Terse, false, "t");

            Assert.Equal(2, _calls.Count);
            Assert.Equal(1300, _calls[1].AtMs);
        }

        [Fact]
        public void Latest_RepeatWhileHeld_TimerIsNotPushedOut_AndRepeatsAreSpacedByMinGap()
        {
            // The #264 flag, pinned: no production caller sets repeatWhileHeld
            // yet, so this path had never been exercised anywhere. Its
            // documented contract: a repeating entry must NOT have its flush
            // deferred by each keypress (the operator is holding the key), the
            // identical value IS re-spoken (the repetition is the information
            // — "still at minimum" is how you learn to stop pressing), and
            // repeats are spaced by MinGap so they cannot chop each other.
            var a = NewArbiter();
            a.Latest("vol", "Volume minimum", VerbosityLevel.Terse, true, "t"); // lead at 0

            _clock.Advance(400);
            a.Latest("vol", "Volume minimum", VerbosityLevel.Terse, true, "t"); // entry, due 700
            _clock.Advance(100);
            a.Latest("vol", "Volume minimum", VerbosityLevel.Terse, true, "t"); // must NOT push to 800
            _clock.Advance(100);
            a.Latest("vol", "Volume minimum", VerbosityLevel.Terse, true, "t"); // must NOT push to 900

            // Flush stays due at 700; MinGap defers the actual utterance to
            // exactly 1200 from the lead.
            _clock.Advance(599);            // t = 1199
            Assert.Single(_calls);
            _clock.Advance(1);              // t = 1200
            Assert.Equal(2, _calls.Count);
            Assert.Equal("Volume minimum", _calls[1].Message);
            Assert.Equal(1200, _calls[1].AtMs);

            // Still holding: the next repeat coalesces at 1300 and speaks at
            // 2400 — MinGap from the previous utterance, never sooner.
            _clock.Advance(100);            // t = 1300
            a.Latest("vol", "Volume minimum", VerbosityLevel.Terse, true, "t");
            _clock.Advance(1099);           // t = 2399
            Assert.Equal(2, _calls.Count);
            _clock.Advance(1);              // t = 2400
            Assert.Equal(3, _calls.Count);
            Assert.Equal(2400, _calls[2].AtMs);
        }

        [Fact]
        public void Latest_VerbosityDroppedWhilePending_IsRecordedGatedNotSpoken()
        {
            var a = NewArbiter();
            a.Latest("rf", "RF gain 5", VerbosityLevel.Chatty, false, "t");
            _clock.Advance(400);
            a.Latest("rf", "RF gain 6", VerbosityLevel.Chatty, false, "t");

            // The operator turns speech down while the settle is pending.
            _verbosity = VerbosityLevel.Critical;
            _clock.Advance(10_000);

            Assert.Single(_calls);
            var gated = Assert.Single(_gated);
            Assert.Equal("RF gain 6", gated.Message);
        }

        // ── The believed-pending ledger: interrupt jumps the queue ──

        [Fact]
        public void Interrupt_SalvagesQueuedBacklog_InOrder_BehindItself()
        {
            // The 2026-08-25 capture, replayed with its own timings: three
            // queued connect messages, then an interrupt from another thread
            // three milliseconds after the third. The old behaviour destroyed
            // all three while the trace said "Spoke". The policy now: the
            // interrupter speaks first, and everything believed unspoken is
            // re-queued behind it, in its original order, marked salvaged.
            var a = NewArbiter();
            a.Emit("Disconnected", false, null, VerbosityLevel.Terse, "MainWindow");
            _clock.Advance(2);
            a.Emit("Session closed", false, null, VerbosityLevel.Terse, "MainWindow");
            _clock.Advance(665);
            a.Emit("6300inshack went offline.", false, null, null, "globals");
            _clock.Advance(3);
            a.Emit("No SmartLink radios available. The remote radio may be turned off.",
                true, null, VerbosityLevel.Critical, "FlexBase");

            Assert.Equal(7, _calls.Count);
            Assert.Equal(
                new[] { "Disconnected", "Session closed", "6300inshack went offline." },
                _calls.Take(3).Select(c => c.Message));
            Assert.All(_calls.Take(3), c => Assert.False(c.Salvaged));

            var interrupter = _calls[3];
            Assert.True(interrupter.Interrupt);
            Assert.StartsWith("No SmartLink radios available", interrupter.Message);
            Assert.False(interrupter.Salvaged);

            Assert.Equal(
                new[] { "Disconnected", "Session closed", "6300inshack went offline." },
                _calls.Skip(4).Select(c => c.Message));
            Assert.All(_calls.Skip(4), c =>
            {
                Assert.True(c.Salvaged);
                Assert.False(c.Interrupt);
            });
        }

        [Fact]
        public void Interrupt_LongAfterBacklogWasHeard_SalvagesNothing()
        {
            var a = NewArbiter();
            a.Emit("Done", false, null, VerbosityLevel.Terse, "t");

            // Five seconds later the estimate says "Done" (SalvageMinMs 800)
            // finished long ago; replaying it would repeat heard speech.
            _clock.Advance(5000);
            a.Emit("Band changed", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t");

            Assert.Equal(2, _calls.Count);
            Assert.DoesNotContain(_calls, c => c.Salvaged);
        }

        [Fact]
        public void SecondInterrupt_SalvagesTheSalvage()
        {
            // A salvaged utterance re-enters the ledger, so back-to-back
            // interrupts cannot do by twos what one is no longer allowed to.
            var a = NewArbiter();
            a.Emit("Session closed", false, null, VerbosityLevel.Terse, "t");
            _clock.Advance(100);
            a.Emit("First", true, SpeechIntent.Interrupt, null, "t");
            _clock.Advance(100);
            a.Emit("Second", true, SpeechIntent.Interrupt, null, "t");

            Assert.Equal(2, _calls.Count(c => c.Salvaged && c.Message == "Session closed"));
        }

        [Fact]
        public void LatestLead_AlsoSalvagesQueuedBacklog()
        {
            // Latest emissions interrupt to supersede their own stale value;
            // they must not silently take unrelated queued speech with them.
            var a = NewArbiter();
            a.Emit("Session closed", false, null, VerbosityLevel.Terse, "t");
            _clock.Advance(100);
            a.Latest("tx", "TX Power 50", VerbosityLevel.Terse, false, "t");

            Assert.Equal(3, _calls.Count);
            Assert.Equal("TX Power 50", _calls[1].Message);
            Assert.True(_calls[2].Salvaged);
            Assert.Equal("Session closed", _calls[2].Message);
        }

        [Fact]
        public void Urgent_DiscardsBacklogAndSilences()
        {
            // Urgent is the one intent for which discard is the point: nothing
            // stale may play on top of a transmit warning.
            var a = NewArbiter();
            a.Emit("Session closed", false, null, VerbosityLevel.Terse, "t");
            _clock.Advance(100);
            a.Urgent("80 percent of your power is coming back on ANT2.",
                VerbosityLevel.Critical, "t");

            Assert.Equal(1, _silenceCount);
            Assert.Equal(2, _calls.Count);
            Assert.True(_calls[1].Interrupt);
            Assert.Equal(SpeechIntent.Urgent, _calls[1].Intent);
            Assert.DoesNotContain(_calls, c => c.Salvaged);

            // And the backlog stays gone: a later interrupt finds nothing.
            _clock.Advance(100);
            a.Emit("Receiving", true, SpeechIntent.Interrupt, VerbosityLevel.Critical, "t");
            Assert.DoesNotContain(_calls, c => c.Salvaged);
        }

        [Fact]
        public void Urgent_DiscardsPendingLatest_AndClearsDedupState()
        {
            var a = NewArbiter();
            a.Latest("tx", "TX Power 5", VerbosityLevel.Terse, false, "t");   // lead at 0
            _clock.Advance(400);
            a.Latest("tx", "TX Power 6", VerbosityLevel.Terse, false, "t");   // pending
            _clock.Advance(100);
            a.Urgent("Warning", VerbosityLevel.Critical, "t");

            // The pending settle died with the discard...
            _clock.Advance(10_000);
            Assert.DoesNotContain(_calls, c => c.Message == "TX Power 6");

            // ...and the dedup state died with it, so the next value speaks
            // instead of being suppressed as a duplicate of discarded speech.
            a.Latest("tx", "TX Power 5", VerbosityLevel.Terse, false, "t");
            Assert.Equal("TX Power 5", _calls[^1].Message);
            Assert.False(_calls[^1].Salvaged);
        }

        [Fact]
        public void Interrupt_ThatNeverReachedTheReader_DoesNotSalvage()
        {
            // A suppressed interrupt never flushed the reader's queue, so
            // "salvaging" would emit duplicates of speech still safely queued.
            var a = NewArbiter();
            a.Emit("Session closed", false, null, VerbosityLevel.Terse, "t");
            _clock.Advance(100);

            _sinkResult = false;   // SuppressSpeech / no backend
            a.Emit("Suppressed", true, SpeechIntent.Interrupt, null, "t");
            Assert.DoesNotContain(_calls, c => c.Salvaged);

            // The ledger survived, so a real interrupt still protects it.
            _sinkResult = true;
            _clock.Advance(100);
            a.Emit("Real", true, SpeechIntent.Interrupt, null, "t");
            Assert.Contains(_calls, c => c.Salvaged && c.Message == "Session closed");
        }

        [Fact]
        public void QueuedUtterance_ThatNeverReachedTheReader_IsNotLedgered()
        {
            var a = NewArbiter();
            _sinkResult = false;
            a.Emit("Never sounded", false, null, VerbosityLevel.Terse, "t");

            _sinkResult = true;
            _clock.Advance(100);
            a.Emit("Interrupter", true, SpeechIntent.Interrupt, null, "t");
            Assert.DoesNotContain(_calls, c => c.Salvaged);
        }

        [Fact]
        public void OnSilenced_ClearsTheBacklog()
        {
            // The operator asked for quiet; resurrecting what they shut up
            // would defy them.
            var a = NewArbiter();
            a.Emit("Session closed", false, null, VerbosityLevel.Terse, "t");
            _clock.Advance(100);
            a.OnSilenced();
            _clock.Advance(100);
            a.Emit("Interrupter", true, SpeechIntent.Interrupt, null, "t");

            Assert.DoesNotContain(_calls, c => c.Salvaged);
        }
    }
}
