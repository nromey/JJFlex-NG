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
    //  manual clock by hand: advance to one millisecond short of a
    //  boundary and assert silence, advance one more and assert exactly
    //  one utterance. CoalesceMs (300) and SweepWindowMs (1200) are
    //  exercised at their exact boundaries.
    //
    //  The anti-clip gap is no longer a constant to pin (#282, 2026-08-27):
    //  it is derived per message from AntiClipGapMs, so each test below
    //  states the arithmetic for ITS OWN message rather than quoting 1200.
    //  "RF gain 5" is nine characters and earns 990 ms; "TX Power 87" is
    //  eleven and hits the 1200 ms ceiling; "S 3" is three and lands on the
    //  700 ms floor. A test that quoted one number for all three would go
    //  green while saying nothing.
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
        public void Latest_SecondValueDuringSweep_SettlesExactlyAtTheGapTheLeadEarned()
        {
            var a = NewArbiter();
            a.Latest("rf", "RF gain 5", VerbosityLevel.Terse, false, "t");   // lead at 0

            _clock.Advance(400);
            a.Latest("rf", "RF gain 6", VerbosityLevel.Terse, false, "t");   // coalesces

            // CoalesceMs would flush at 700, but the lead is still speaking:
            // "RF gain 5" is nine characters, so the anti-clip gap is 990 ms
            // and the settle waits it out rather than cutting in. Nothing may
            // speak at 700, nothing at 989 — and at exactly 990 the settle
            // fires once with the newest value.
            //
            // 990, not 1200. Under the old flat floor this deliberate second
            // press waited 210 ms longer than the lead needed, for nothing.
            int gap = Radios.Speech.SpeechArbiter.AntiClipGapMs("RF gain 5");
            Assert.Equal(990, gap);

            _clock.Advance(299);            // t = 699
            Assert.Single(_calls);
            _clock.Advance(1);              // t = 700 — coalesce due, gap defers
            Assert.Single(_calls);
            _clock.Advance(gap - 700 - 1);  // t = 989
            Assert.Single(_calls);
            _clock.Advance(1);              // t = 990
            Assert.Equal(2, _calls.Count);
            Assert.Equal("RF gain 6", _calls[1].Message);
            Assert.Equal(gap, _calls[1].AtMs);
            Assert.True(_calls[1].Interrupt);
        }

        [Fact]
        public void Latest_ShortReadout_SettlesOnTheFloor_NotTheCeiling()
        {
            // #282, the operator-visible half: a short readout must not be
            // charged a long sentence's price. "S 3" is three characters, so
            // the derived gap lands on the 700 ms floor — and the settle can
            // therefore speak the moment the coalesce timer's own deferral is
            // satisfied, 500 ms earlier than the old flat 1200.
            var a = NewArbiter();
            a.Latest("smeter", "S 3", VerbosityLevel.Terse, false, "t");     // lead at 0

            _clock.Advance(400);
            a.Latest("smeter", "S 2", VerbosityLevel.Terse, false, "t");     // coalesces

            Assert.Equal(700, Radios.Speech.SpeechArbiter.AntiClipGapMs("S 3"));

            _clock.Advance(299);            // t = 699
            Assert.Single(_calls);
            _clock.Advance(1);              // t = 700 — coalesce due AND gap clear
            Assert.Equal(2, _calls.Count);
            Assert.Equal("S 2", _calls[1].Message);
            Assert.Equal(700, _calls[1].AtMs);
        }

        [Fact]
        public void Latest_LongReadout_StillWaitsTheFullCeiling()
        {
            // The other direction, and the reason the ceiling exists: the
            // 2026-08-18 clipping regression was measured on exactly this
            // message. "TX Power 87" is eleven characters — 1210 ms derived,
            // clamped to the 1200 ms ceiling — so this case is UNCHANGED by
            // #282. The gap may only ever get shorter than it used to be.
            var a = NewArbiter();
            a.Latest("tx", "TX Power 87", VerbosityLevel.Terse, false, "t"); // lead at 0

            _clock.Advance(400);
            a.Latest("tx", "TX Power 86", VerbosityLevel.Terse, false, "t"); // coalesces

            Assert.Equal(1200, Radios.Speech.SpeechArbiter.AntiClipGapMs("TX Power 87"));

            _clock.Advance(799);            // t = 1199
            Assert.Single(_calls);
            _clock.Advance(1);              // t = 1200
            Assert.Equal(2, _calls.Count);
            Assert.Equal(1200, _calls[1].AtMs);
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

            // "TX Power 5" is ten characters, so the lead's anti-clip gap is
            // 1100 ms — exactly when the pushed-out flush comes due, so the
            // settle speaks without any further deferral.
            Assert.Equal(1100, Radios.Speech.SpeechArbiter.AntiClipGapMs("TX Power 5"));

            _clock.Advance(299);            // t = 1099
            Assert.Single(_calls);
            _clock.Advance(1);              // t = 1100
            Assert.Equal(2, _calls.Count);
            Assert.Equal("TX Power 8", _calls[1].Message);
            Assert.Equal(1100, _calls[1].AtMs);

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
        public void Latest_RepeatWhileHeld_TimerIsNotPushedOut_AndRepeatsAreSpacedByTheGap()
        {
            // The #264 flag, pinned. Its documented contract: a repeating
            // entry must NOT have its flush deferred by each keypress (the
            // operator is holding the key), the identical value IS re-spoken
            // (the repetition is the information — "still at minimum" is how
            // you learn to stop pressing), and repeats are spaced by the
            // anti-clip gap so they cannot chop each other.
            //
            // This said "no production caller sets repeatWhileHeld yet" until
            // 2026-08-27, and it had been false since Sprint 35 Track M wired
            // Ctrl+S (KeyCommands.cs, coalesceKey "smeter"). That call site is
            // still the ONLY one, deliberately — see the #264 note there.
            //
            // "Volume minimum" is fourteen characters, so its derived gap hits
            // the 1200 ms ceiling and the cadence below is unchanged by #282.
            var a = NewArbiter();
            a.Latest("vol", "Volume minimum", VerbosityLevel.Terse, true, "t"); // lead at 0

            _clock.Advance(400);
            a.Latest("vol", "Volume minimum", VerbosityLevel.Terse, true, "t"); // entry, due 700
            _clock.Advance(100);
            a.Latest("vol", "Volume minimum", VerbosityLevel.Terse, true, "t"); // must NOT push to 800
            _clock.Advance(100);
            a.Latest("vol", "Volume minimum", VerbosityLevel.Terse, true, "t"); // must NOT push to 900

            // Flush stays due at 700; the gap defers the actual utterance to
            // exactly 1200 from the lead.
            _clock.Advance(599);            // t = 1199
            Assert.Single(_calls);
            _clock.Advance(1);              // t = 1200
            Assert.Equal(2, _calls.Count);
            Assert.Equal("Volume minimum", _calls[1].Message);
            Assert.Equal(1200, _calls[1].AtMs);

            // Still holding: the next repeat coalesces at 1300 and speaks at
            // 2400 — a full gap from the previous utterance, never sooner.
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

        // ── #273: the rescue is bounded ──

        /// <summary>
        /// The sentence from the 2026-08-26 capture, verbatim. Its length is
        /// load-bearing: it is what makes the estimated speaking window long
        /// enough for the runaway to keep finding itself still "pending".
        /// </summary>
        private const string CaptureStarted =
            "Detailed capture started. Reproduce the problem, then stop the capture "
            + "from this button or the Diagnostics tab.";

        [Fact]
        public void Salvage_StopsAtTheCap_InsteadOfRenewingItsOwnLease()
        {
            // THE FIELD CASE, REPLAYED. 2026-08-26: a detailed capture was
            // running, so its start announcement sat in the ledger; the
            // operator then pressed Ctrl+S nine times. Every press interrupted,
            // every interrupt rescued the same sentence, and each rescue took a
            // fresh lease that pushed the believed-busy window further out —
            // manufacturing the very window that justified the next rescue.
            // Nine presses, TEN salvages of one sentence, each arriving later
            // than the last, and nothing in the mechanism would have stopped
            // it.
            var a = NewArbiter();
            a.Emit(CaptureStarted, false, null, VerbosityLevel.Terse, "TraceAdmin");

            for (int i = 1; i <= 9; i++)
            {
                _clock.Advance(600);
                a.Emit("S " + i, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "KeyCommands");
            }

            // Every reading still speaks — the readings were never the problem.
            Assert.Equal(9, _calls.Count(c => !c.Salvaged && c.Interrupt));

            // The sentence is rescued twice and then dropped, not nine or ten
            // times. Two, because a SECOND interrupt must still not destroy
            // what the first one had to rescue; a third is chasing a burst it
            // will not get ahead of.
            var salvaged = _calls.Where(c => c.Salvaged).ToList();
            Assert.Equal(Radios.Speech.SpeechArbiter.MaxSalvages, salvaged.Count);
            Assert.All(salvaged, c => Assert.Equal(CaptureStarted, c.Message));

            // And it stops EARLY, not merely eventually: both rescues land in
            // the first two presses, and presses three through nine carry
            // nothing stale behind them at all.
            Assert.Equal(new double[] { 600, 1200 }, salvaged.Select(c => c.AtMs));
        }

        [Fact]
        public void Salvage_RefusedOnceOlderThanItsOwnDuration_MeasuredFromFirstEmission()
        {
            // The second bound, and the one the cap cannot cover: two
            // interrupts far enough apart that the rescue count never reaches
            // its limit, but the utterance is long past describing anything
            // current. Age is measured from FIRST emission — measuring from
            // the latest re-queue is the defect itself, since that lets an
            // utterance renew its own youth.
            int est = Radios.Speech.SpeechArbiter.EstimateSpokenMs(CaptureStarted);
            int ageBound = est * Radios.Speech.SpeechArbiter.SalvageAgeMultiple;
            int interrupterMs = Radios.Speech.SpeechArbiter.EstimateSpokenMs("Now");

            var a = NewArbiter();
            a.Emit(CaptureStarted, false, null, VerbosityLevel.Terse, "TraceAdmin");

            // Just before the first estimate expires: rescued, count now 1.
            int firstInterruptAt = est - 100;
            _clock.Advance(firstInterruptAt);
            a.Emit("Now", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t");
            Assert.Single(_calls.Where(c => c.Salvaged));

            // That rescue re-entered the ledger with a fresh lease, which is
            // correct — the reader really is going to be busy that long again.
            // So the entry is still believed pending here, which is precisely
            // the window the runaway lived in. What it does NOT get back is its
            // age: it is now older than twice its own duration, and it goes.
            int reLeasedFinish = firstInterruptAt + interrupterMs + est;
            int secondInterruptAt = 2 * est + 350;
            Assert.True(secondInterruptAt > ageBound,
                "the second interrupt must fall past the age bound, or nothing is being tested");
            Assert.True(secondInterruptAt < reLeasedFinish,
                "and inside the lease the rescue itself renewed, or the ordinary prune would "
                + "have removed the entry and the age bound would never be consulted");

            _clock.Advance(secondInterruptAt - firstInterruptAt);
            a.Emit("Later", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t");

            Assert.Single(_calls.Where(c => c.Salvaged));
        }

        [Fact]
        public void Salvage_TwiceInQuickSuccession_IsStillAllowed_TheAgeBoundIsWhatRefused()
        {
            // POSITIVE CONTROL for the test above. Same message, same two
            // interrupts, same arbiter — only the spacing differs. If this went
            // green with one salvage too, the previous test would be measuring
            // something other than the age bound and neither would be evidence.
            var a = NewArbiter();
            a.Emit(CaptureStarted, false, null, VerbosityLevel.Terse, "TraceAdmin");

            _clock.Advance(100);
            a.Emit("Now", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t");
            _clock.Advance(100);
            a.Emit("Later", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t");

            Assert.Equal(2, _calls.Count(c => c.Salvaged));
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
