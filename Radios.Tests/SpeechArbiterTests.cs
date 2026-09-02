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
        //
        // The virtual clock moved to FakeSpeechClock.cs on 2026-08-27 (#285)
        // so SpeechCoalescerTimingTests could stop sleeping and share it.

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

            a.Latest("rf", "RF gain 5", VerbosityLevel.Terse, SpeechCoalesceKind.Value, "t");

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
            a.Latest("rf", "RF gain 5", VerbosityLevel.Terse, SpeechCoalesceKind.Value, "t");   // lead at 0

            _clock.Advance(400);
            a.Latest("rf", "RF gain 6", VerbosityLevel.Terse, SpeechCoalesceKind.Value, "t");   // coalesces

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
            a.Latest("smeter", "S 3", VerbosityLevel.Terse, SpeechCoalesceKind.Value, "t");     // lead at 0

            _clock.Advance(400);
            a.Latest("smeter", "S 2", VerbosityLevel.Terse, SpeechCoalesceKind.Value, "t");     // coalesces

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
            a.Latest("tx", "TX Power 87", VerbosityLevel.Terse, SpeechCoalesceKind.Value, "t"); // lead at 0

            _clock.Advance(400);
            a.Latest("tx", "TX Power 86", VerbosityLevel.Terse, SpeechCoalesceKind.Value, "t"); // coalesces

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
            a.Latest("tx", "TX Power 5", VerbosityLevel.Terse, SpeechCoalesceKind.Value, "t");  // lead at 0

            // A debounce, not a throttle: each new value pushes the flush out
            // by CoalesceMs, so the sweep speaks once when the operator stops.
            _clock.Advance(400);
            a.Latest("tx", "TX Power 6", VerbosityLevel.Terse, SpeechCoalesceKind.Value, "t");  // due 700
            _clock.Advance(200);
            a.Latest("tx", "TX Power 7", VerbosityLevel.Terse, SpeechCoalesceKind.Value, "t");  // due 900
            _clock.Advance(200);
            a.Latest("tx", "TX Power 8", VerbosityLevel.Terse, SpeechCoalesceKind.Value, "t");  // due 1100

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
            a.Latest("rf", "RF gain 5", VerbosityLevel.Terse, SpeechCoalesceKind.Value, "t");
            _clock.Advance(400);
            a.Latest("rf", "RF gain 5", VerbosityLevel.Terse, SpeechCoalesceKind.Value, "t");   // same text

            // The settle would only repeat what the lead already said.
            _clock.Advance(10_000);
            Assert.Single(_calls);
        }

        [Fact]
        public void Latest_AfterSweepWindowExpires_LeadsAgainImmediately()
        {
            var a = NewArbiter();
            a.Latest("rf", "RF gain 5", VerbosityLevel.Terse, SpeechCoalesceKind.Value, "t");

            // 1300 ms later the key is no longer sweeping (SweepWindowMs 1200)
            // and MinGap has elapsed: a deliberate press answers instantly.
            _clock.Advance(1300);
            a.Latest("rf", "RF gain 6", VerbosityLevel.Terse, SpeechCoalesceKind.Value, "t");

            Assert.Equal(2, _calls.Count);
            Assert.Equal(1300, _calls[1].AtMs);
        }

        [Fact]
        public void Latest_HeldQueryKey_TimerIsNotPushedOut_AndRepeatsAreSpacedByTheGap()
        {
            // A HELD query key, pinned. Its contract: a query entry must NOT
            // have its flush deferred by each keypress (the operator is asking,
            // and deferring their own answer for as long as they keep asking is
            // the bug), the identical value IS re-spoken (the repetition is the
            // information — "still at minimum" is how you learn to stop
            // pressing), and repeats are spaced by the anti-clip gap so they
            // cannot chop each other.
            //
            // **That gap spacing is the reason Query does not simply bypass the
            // gap.** Under a genuine key repeat this is what turns a held query
            // into a readable cadence; without it the same hold would produce
            // an interrupting utterance per repeat, each cut off after about a
            // phoneme — the "r r r r r RF gain 5" defect of 2026-08-18, rebuilt
            // on the one key most likely to be hammered.
            //
            // This was expressed as a repeatWhileHeld flag until 2026-08-27 and
            // is now SpeechCoalesceKind.Query (#264). The timings below are
            // UNCHANGED by that: the flag and the kind produce the same flush
            // instants here, which is the evidence that Query subsumes it
            // rather than merely resembling it.
            //
            // "Volume minimum" is fourteen characters, so its derived gap hits
            // the 1200 ms ceiling and the cadence below is unchanged by #282.
            var a = NewArbiter();
            a.Latest("vol", "Volume minimum", VerbosityLevel.Terse, SpeechCoalesceKind.Query, "t"); // lead at 0

            _clock.Advance(400);
            a.Latest("vol", "Volume minimum", VerbosityLevel.Terse, SpeechCoalesceKind.Query, "t"); // entry, due 700
            _clock.Advance(100);
            a.Latest("vol", "Volume minimum", VerbosityLevel.Terse, SpeechCoalesceKind.Query, "t"); // must NOT push to 800
            _clock.Advance(100);
            a.Latest("vol", "Volume minimum", VerbosityLevel.Terse, SpeechCoalesceKind.Query, "t"); // must NOT push to 900

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
            a.Latest("vol", "Volume minimum", VerbosityLevel.Terse, SpeechCoalesceKind.Query, "t");
            _clock.Advance(1099);           // t = 2399
            Assert.Equal(2, _calls.Count);
            _clock.Advance(1);              // t = 2400
            Assert.Equal(3, _calls.Count);
            Assert.Equal(2400, _calls[2].AtMs);
        }

        // ── #264: a key that asks a question is not a value that sweeps ──
        //
        //  Measured at the radio 2026-08-27: a second Ctrl+S was still about
        //  half a second late after the anti-clip gap had already been fixed,
        //  because SweepWindowMs classified ANY second press inside 1.2 s as
        //  sweeping a value and gave it the settle treatment. Hammering Ctrl+S
        //  is not sweeping anything — it is asking the same question again,
        //  and a sweep wants the tail while a re-request wants an answer now.
        //
        //  Each test below is PAIRED with its Value control at identical
        //  timings. That pairing is the point: it shows the change is a
        //  classification and not a tuning, because the Value figures are the
        //  ones the sweep tests above already pin and they do not move.

        [Fact]
        public void Latest_QueryRePressPastTheGap_AnswersAtOnce_WhereAValueWouldSettle()
        {
            // "S 3" is three characters, so the gap it earns is the 700 ms
            // floor. A re-press at 800 ms is therefore past the gap but well
            // inside the 1200 ms sweep window — the exact window in which the
            // operator's second press used to be misread as a sweep.
            var a = NewArbiter();
            a.Latest("smeter", "S 3", VerbosityLevel.Terse, SpeechCoalesceKind.Query, "t");
            Assert.Equal(700, Radios.Speech.SpeechArbiter.AntiClipGapMs("S 3"));

            _clock.Advance(800);
            a.Latest("smeter", "S 5", VerbosityLevel.Terse, SpeechCoalesceKind.Query, "t");

            // Synchronously, inside the call — no timer involved at all.
            Assert.Equal(2, _calls.Count);
            Assert.Equal("S 5", _calls[1].Message);
            Assert.Equal(800, _calls[1].AtMs);
        }

        [Fact]
        public void Latest_ValueRePressPastTheGap_StillSettles_TheControlForTheQueryCase()
        {
            // THE CONTROL, at timings identical to the test above. A swept
            // value inside the sweep window keeps the settle: nothing at 800,
            // and the coalesce timer speaks at 1100. That 300 ms is the settle
            // #264 removed from queries and deliberately left here, because a
            // value in flight genuinely wants its tail.
            var a = NewArbiter();
            a.Latest("rf", "RF gain 5", VerbosityLevel.Terse, SpeechCoalesceKind.Value, "t");

            _clock.Advance(800);
            a.Latest("rf", "RF gain 6", VerbosityLevel.Terse, SpeechCoalesceKind.Value, "t");
            Assert.Single(_calls);          // still just the lead

            _clock.Advance(299);            // t = 1099
            Assert.Single(_calls);
            _clock.Advance(1);              // t = 1100
            Assert.Equal(2, _calls.Count);
            Assert.Equal(1100, _calls[1].AtMs);
        }

        [Fact]
        public void Latest_QueryRePressInsideTheGap_WaitsTheGapOnly_NotTheSettleAsWell()
        {
            // A query CAN still be deferred, by the anti-clip gap — that is
            // physical, not policy, and it is what keeps a hammered query from
            // cutting itself into clicks. What it must not do is pay the settle
            // on top: pressing at 550 ms into a 700 ms gap should be answered
            // at 700, when the previous reading is out of the way, and not at
            // 850 (550 + CoalesceMs), which is what arming the settle first
            // would cost.
            var a = NewArbiter();
            a.Latest("smeter", "S 3", VerbosityLevel.Terse, SpeechCoalesceKind.Query, "t");

            _clock.Advance(550);
            a.Latest("smeter", "S 5", VerbosityLevel.Terse, SpeechCoalesceKind.Query, "t");

            _clock.Advance(149);            // t = 699
            Assert.Single(_calls);
            _clock.Advance(1);              // t = 700 — exactly the gap
            Assert.Equal(2, _calls.Count);
            Assert.Equal("S 5", _calls[1].Message);
            Assert.Equal(700, _calls[1].AtMs);
        }

        [Fact]
        public void Latest_QueryRepeatedIdenticalReading_IsSpokenAgain()
        {
            // The other half of the report: on a steady signal the second press
            // said NOTHING AT ALL, because the settle's duplicate-drop treated
            // an unchanged reading as nothing new. On a meter the repetition is
            // the whole answer — it is how the operator learns the signal has
            // not moved — and a key that answers with silence is
            // indistinguishable from a key that is broken.
            var a = NewArbiter();
            a.Latest("smeter", "S 7", VerbosityLevel.Terse, SpeechCoalesceKind.Query, "t");

            _clock.Advance(800);
            a.Latest("smeter", "S 7", VerbosityLevel.Terse, SpeechCoalesceKind.Query, "t");

            Assert.Equal(2, _calls.Count);
            Assert.Equal("S 7", _calls[1].Message);
            Assert.Equal(800, _calls[1].AtMs);
        }

        [Fact]
        public void Latest_ValueRepeatedIdenticalValue_IsStillDropped_TheControl()
        {
            // THE CONTROL for the drop. A swept value that settles on the same
            // reading the lead already announced still says nothing, and must:
            // repeating it would cut the lead off to tell the operator
            // something they have just been told.
            var a = NewArbiter();
            a.Latest("rf", "RF gain 5", VerbosityLevel.Terse, SpeechCoalesceKind.Value, "t");

            _clock.Advance(800);
            a.Latest("rf", "RF gain 5", VerbosityLevel.Terse, SpeechCoalesceKind.Value, "t");

            _clock.Advance(10_000);
            Assert.Single(_calls);
        }

        [Fact]
        public void Latest_SweepWindowIsUntouched_ByTheQueryClassification()
        {
            // #264 said explicitly: do NOT fix this by tuning SweepWindowMs,
            // which would degrade the sweeps that constant exists for. This
            // pins the constant so a later "just shorten the window" edit
            // fails here rather than quietly making every sweep chatter.
            Assert.Equal(1200, Radios.Speech.SpeechArbiter.SweepWindowMs);
            Assert.Equal(300, Radios.Speech.SpeechArbiter.CoalesceMs);
        }

        [Fact]
        public void Latest_VerbosityDroppedWhilePending_IsRecordedGatedNotSpoken()
        {
            var a = NewArbiter();
            a.Latest("rf", "RF gain 5", VerbosityLevel.Chatty, SpeechCoalesceKind.Value, "t");
            _clock.Advance(400);
            a.Latest("rf", "RF gain 6", VerbosityLevel.Chatty, SpeechCoalesceKind.Value, "t");

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
            a.Latest("tx", "TX Power 50", VerbosityLevel.Terse, SpeechCoalesceKind.Value, "t");

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
            a.Latest("tx", "TX Power 5", VerbosityLevel.Terse, SpeechCoalesceKind.Value, "t");   // lead at 0
            _clock.Advance(400);
            a.Latest("tx", "TX Power 6", VerbosityLevel.Terse, SpeechCoalesceKind.Value, "t");   // pending
            _clock.Advance(100);
            a.Urgent("Warning", VerbosityLevel.Critical, "t");

            // The pending settle died with the discard...
            _clock.Advance(10_000);
            Assert.DoesNotContain(_calls, c => c.Message == "TX Power 6");

            // ...and the dedup state died with it, so the next value speaks
            // instead of being suppressed as a duplicate of discarded speech.
            a.Latest("tx", "TX Power 5", VerbosityLevel.Terse, SpeechCoalesceKind.Value, "t");
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

        // ── #503: a rescue is refused by SUPERSESSION, not by age ──
        //
        //  Measured across every trace of 2026-09-01: 89 dropped salvages,
        //  52 of them never re-spoken even once. The bound was twice the
        //  utterance's own word-count estimate, so "SWR 1.7" — seven
        //  characters, the answer to the tune the operator had just run —
        //  was allowed 1,600 ms and binned at 3,863 ms by the operator
        //  pressing Tune AGAIN because they had heard nothing. Nothing in the
        //  arbiter expires on its own clock; an entry is judged only when the
        //  next interrupt arrives, so the retry a lost answer provokes was
        //  the very event that destroyed the answer.
        //
        //  Now an entry is worthless when something newer covers its SUBJECT
        //  (SpeechSubject), under an absolute ceiling — and an entry that
        //  declared no subject keeps the old bound, because for it the
        //  arbiter cannot know what would supersede it. Every test below has
        //  a control at identical timings without the subject, so a green
        //  result cannot come from the timings alone.
        //
        //  Several tests queue a long line AHEAD of the short one under test.
        //  That is not decoration: it is what was in the reader's queue on
        //  the day, and it is what keeps a short line believed pending past
        //  its own estimate. Without it the ordinary prune removes the entry
        //  as heard and no bound is consulted at all.

        /// <summary>The connect summary that sat ahead of "PC audio on." in the 213210 session.</summary>
        private const string ConnectSummary =
            "Connected to FLEX-6300, SmartLink. 2 slices. Slice A, yours, transmit, "
            + "14.100 megahertz, USB, pan center.";

        /// <summary>The launch greeting, ahead of the discovery narration at every start.</summary>
        private const string Greeting =
            "JJ Flexible Radio Access. Press F1 on any control for help, "
            + "or Control slash to find a command.";

        /// <summary>The 300-character courtesy the old bound gave thirty seconds, verbatim from the day.</summary>
        private const string MicProfileHeadsUp =
            "Heads up: this radio has no mic profile selected. Until one is loaded, audio from "
            + "your computer will not be transmitted through your radio — you would key up and "
            + "nobody would hear you. Nothing you did caused it, and receive is unaffected. The "
            + "Audio Workshop has the details.";

        [Fact]
        public void Swr17_TheRetryDeliversTheAnswerInsteadOfDestroyingIt()
        {
            // JJFlexRadioTrace-20260901-213333.txt, ticks 5264910 to 5268774,
            // replayed at its own offsets: "Tune off"; the reading queued one
            // millisecond later; an unrelated interrupt at +1,015 ms that
            // rescued it once; and Tune pressed again at +3,863 ms — the
            // operator's retry — which found it 3,863 ms old against a
            // 1,600 ms bound and binned it. That session did not record
            // utterance text, so the +1,015 interrupter here is a stand-in;
            // it is long enough to keep the reading believed pending, as the
            // real one must have been for the drop line to exist at all.
            var a = NewArbiter();
            a.Emit("Tune off", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "MainWindow");
            _clock.Advance(1);
            a.Emit("SWR 1.7", false, SpeechIntent.Queue, VerbosityLevel.Terse, "MainWindow",
                subject: SpeechSubject.SwrAfterTune);
            _clock.Advance(1015);
            a.Emit("Receiver controls collapsed, twelve fields hidden", true,
                SpeechIntent.Interrupt, VerbosityLevel.Terse, "MainWindow");
            _clock.Advance(3863 - 1015);
            a.Emit("Tune on", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "MainWindow");

            var rescues = _calls.Where(c => c.Salvaged && c.Message == "SWR 1.7").ToList();
            Assert.Equal(new double[] { 1016, 3864 }, rescues.Select(c => c.AtMs));

            // The answer is the last thing handed over — behind the retry,
            // which is exactly where an operator who pressed Tune again
            // because they heard nothing needs it.
            Assert.Equal("SWR 1.7", _calls[^1].Message);
            Assert.True(_calls[^1].Salvaged);
        }

        [Fact]
        public void Swr17_TheControl_UnkeyedTheSameSequenceStillLosesIt()
        {
            // Identical timings, no subject. This is the 2026-09-01 outcome,
            // and it must STAY the outcome for an utterance whose emitter
            // declared nothing: the arbiter has no honest way to know that a
            // seven-character message is a state fact rather than a stale
            // digit, so it keeps the conservative bound. If this went green
            // with two rescues, the test above would be measuring nothing.
            var a = NewArbiter();
            a.Emit("Tune off", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "MainWindow");
            _clock.Advance(1);
            a.Emit("SWR 1.7", false, SpeechIntent.Queue, VerbosityLevel.Terse, "MainWindow");
            _clock.Advance(1015);
            a.Emit("Receiver controls collapsed, twelve fields hidden", true,
                SpeechIntent.Interrupt, VerbosityLevel.Terse, "MainWindow");
            _clock.Advance(3863 - 1015);
            a.Emit("Tune on", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "MainWindow");

            var rescues = _calls.Where(c => c.Salvaged && c.Message == "SWR 1.7").ToList();
            Assert.Equal(new double[] { 1016 }, rescues.Select(c => c.AtMs));
            Assert.Equal("Tune on", _calls[^1].Message);
        }

        [Fact]
        public void Salvage_KeyedStateFact_SurvivesALaterUnrelatedInterrupt()
        {
            // "PC audio on." is twelve characters — a 1,920 ms bound under
            // the old policy — and was dropped five times on 2026-09-01
            // between 2.2 and 5.6 s old, never re-spoken, on a remote
            // connection where it is the only reason the operator can hear
            // the radio at all. Timings from the 204423 session, tick 2677826.
            var a = NewArbiter();
            a.Emit(ConnectSummary, false, SpeechIntent.Queue, VerbosityLevel.Terse, "connect");
            a.Emit("PC audio on.", false, SpeechIntent.Queue, VerbosityLevel.Terse, "MainWindow",
                subject: SpeechSubject.PcAudio);
            _clock.Advance(3291);
            a.Emit("JJ Flexible Home, slice, 14.100.000", true, SpeechIntent.Interrupt,
                VerbosityLevel.Terse, "Home");

            Assert.Contains(_calls, c => c.Salvaged && c.Message == "PC audio on.");
        }

        [Fact]
        public void Salvage_UnkeyedStateFact_TheControl_IsStillDroppedByTheWordCountBound()
        {
            var a = NewArbiter();
            a.Emit(ConnectSummary, false, SpeechIntent.Queue, VerbosityLevel.Terse, "connect");
            a.Emit("PC audio on.", false, SpeechIntent.Queue, VerbosityLevel.Terse, "MainWindow");
            _clock.Advance(3291);
            a.Emit("JJ Flexible Home, slice, 14.100.000", true, SpeechIntent.Interrupt,
                VerbosityLevel.Terse, "Home");

            Assert.DoesNotContain(_calls, c => c.Salvaged && c.Message == "PC audio on.");

            // The summary ahead of it is unkeyed too, and long: its bound is
            // far off, so it IS rescued. That is the inversion #503 names —
            // the paragraph lives, the fact dies — and it is deliberately
            // left in place for unkeyed utterances rather than replaced by a
            // guess about which of them are facts.
            Assert.Contains(_calls, c => c.Salvaged && c.Message == ConnectSummary);
        }

        [Fact]
        public void Salvage_TypedDigits_DoNotSurviveTheValueTheyBuilt()
        {
            // JJFlexRadioTrace-20260901-202636.txt, ticks 88490 to 90147:
            // "1", "5", Enter, "Tune Power 15", then Tune. The digits were
            // dropped that day too — at 1,656 and 1,607 ms against a
            // 1,600 ms bound, by luck of arithmetic. Here the interrupt
            // comes 300 ms after the first digit, well inside the old bound,
            // and they are STILL not rescued: the entry has ended in a value
            // and the value covers them. The reason is the value, not the
            // clock.
            string entry = SpeechSubject.ValueEntry("Tune Power");
            string field = SpeechSubject.ValueField("Tune Power");
            var a = NewArbiter();
            a.Emit("Enter Tune Power value", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "field");
            _clock.Advance(1);
            a.Emit("1", false, SpeechIntent.Queue, VerbosityLevel.Critical, "field",
                subject: entry, additive: true);
            _clock.Advance(48);
            a.Emit("5", false, SpeechIntent.Queue, VerbosityLevel.Critical, "field",
                subject: entry, additive: true);
            _clock.Advance(208);
            a.Supersede(entry, "the entry ending as 'Tune Power 15'", "field");
            a.Emit("Tune Power 15", false, SpeechIntent.Queue, VerbosityLevel.Terse, "field",
                subject: field);
            _clock.Advance(43);
            a.Emit("Tune on", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "MainWindow");

            Assert.Equal(new[] { "Tune Power 15" },
                _calls.Where(c => c.Salvaged).Select(c => c.Message));
        }

        [Fact]
        public void Salvage_TypedDigits_MidEntry_AreAllRescuedInOrder()
        {
            // Why the echoes are ADDITIVE: a digit must NOT supersede the
            // digit before it. Interrupted mid-entry, an operator who has
            // typed "1" then "5" needs to hear "1, 5" again — not a lone "5"
            // over a field that reads 15. The first version of this policy
            // got exactly that wrong, and this test is what caught it.
            string entry = SpeechSubject.ValueEntry("Tune Power");
            var a = NewArbiter();
            a.Emit("Enter Tune Power value", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "field");
            _clock.Advance(1);
            a.Emit("1", false, SpeechIntent.Queue, VerbosityLevel.Critical, "field",
                subject: entry, additive: true);
            _clock.Advance(48);
            a.Emit("5", false, SpeechIntent.Queue, VerbosityLevel.Critical, "field",
                subject: entry, additive: true);
            _clock.Advance(251);
            a.Emit("Band changed", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t");

            Assert.Equal(new[] { "1", "5" },
                _calls.Where(c => c.Salvaged).Select(c => c.Message));
        }

        [Fact]
        public void Salvage_TypedDigits_TheControl_RestatingEchoesWouldLoseTheFirst()
        {
            // The same two digits emitted as restatements: "5" covers "1" and
            // only "5" comes back. This is the wrong behaviour for an entry,
            // pinned here so the additive flag cannot quietly become a no-op.
            string entry = SpeechSubject.ValueEntry("Tune Power");
            var a = NewArbiter();
            a.Emit("Enter Tune Power value", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "field");
            _clock.Advance(1);
            a.Emit("1", false, SpeechIntent.Queue, VerbosityLevel.Critical, "field", subject: entry);
            _clock.Advance(48);
            a.Emit("5", false, SpeechIntent.Queue, VerbosityLevel.Critical, "field", subject: entry);
            _clock.Advance(251);
            a.Emit("Band changed", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t");

            Assert.Equal(new[] { "5" },
                _calls.Where(c => c.Salvaged).Select(c => c.Message));
        }

        [Fact]
        public void Salvage_TypedDigits_TheControl_UnkeyedYoungDigitsAreRescuedOverTheValue()
        {
            // Identical timings, nothing keyed and nothing retired: the old
            // outcome for digits younger than their bound — all three
            // re-spoken, "1", "5" and then the value, which is precisely the
            // stale echo the supersession above exists to stop.
            var a = NewArbiter();
            a.Emit("Enter Tune Power value", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "field");
            _clock.Advance(1);
            a.Emit("1", false, SpeechIntent.Queue, VerbosityLevel.Critical, "field");
            _clock.Advance(48);
            a.Emit("5", false, SpeechIntent.Queue, VerbosityLevel.Critical, "field");
            _clock.Advance(208);
            a.Emit("Tune Power 15", false, SpeechIntent.Queue, VerbosityLevel.Terse, "field");
            _clock.Advance(43);
            a.Emit("Tune on", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "MainWindow");

            Assert.Equal(new[] { "1", "5", "Tune Power 15" },
                _calls.Where(c => c.Salvaged).Select(c => c.Message));
        }

        [Fact]
        public void Salvage_ProgressLine_DoesNotSurviveTheNextProgressLine()
        {
            // 2026-09-01, four sessions in a row: "Looking for radios on your
            // network." dropped between 9.1 and 9.8 s old and "Still looking
            // for radios." between 5.1 and 5.8 s, all by the interrupt that
            // ended discovery. Correctly dropped — and now for the right
            // reason: each line covers the one before it.
            var a = NewArbiter();
            a.Emit(Greeting, false, SpeechIntent.Queue, VerbosityLevel.Terse, "launch");
            a.Emit("Looking for radios on your network.", false, SpeechIntent.Queue,
                VerbosityLevel.Terse, "ProgressVoice", subject: SpeechSubject.Progress);
            _clock.Advance(4000);
            a.Emit("Still looking for radios.", false, SpeechIntent.Queue,
                VerbosityLevel.Terse, "ProgressVoice", subject: SpeechSubject.Progress);
            _clock.Advance(1000);
            a.Emit("Discovering radios", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "dialog");

            var rescued = _calls.Where(c => c.Salvaged).Select(c => c.Message).ToList();
            Assert.DoesNotContain("Looking for radios on your network.", rescued);
            Assert.Contains("Still looking for radios.", rescued);
        }

        [Fact]
        public void Supersede_RetiresTheLastProgressLine_WhenTheWaitEnds()
        {
            // The last "still looking" is covered by nothing that is itself a
            // progress line — the dialog that answers it does that. So the
            // voice says so explicitly when it stops, and the next interrupt
            // rescues neither line. The greeting, unkeyed and unrelated, is
            // still rescued: retiring one subject burns nothing else.
            var a = NewArbiter();
            a.Emit(Greeting, false, SpeechIntent.Queue, VerbosityLevel.Terse, "launch");
            a.Emit("Looking for radios on your network.", false, SpeechIntent.Queue,
                VerbosityLevel.Terse, "ProgressVoice", subject: SpeechSubject.Progress);
            _clock.Advance(4000);
            a.Emit("Still looking for radios.", false, SpeechIntent.Queue,
                VerbosityLevel.Terse, "ProgressVoice", subject: SpeechSubject.Progress);
            _clock.Advance(1000);
            a.Supersede(SpeechSubject.Progress,
                "the end of the wait for 'local discovery' (dialog announced: Discovering radios)",
                "ProgressVoice");
            _clock.Advance(500);
            a.Emit("Discovering radios", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "dialog");

            Assert.Equal(new[] { Greeting },
                _calls.Where(c => c.Salvaged).Select(c => c.Message));
        }

        [Fact]
        public void Salvage_ProgressLines_TheControl_UnkeyedBothAreRescuedByTheirOwnBounds()
        {
            // Same timings, no subject. "Looking…" is 35 characters, a
            // 5,600 ms bound, and 5,000 ms old: rescued. "Still looking…"
            // is 25, a 4,000 ms bound, and 1,000 ms old: rescued. Both are
            // re-spoken behind the dialog that made them meaningless. The
            // old policy dropped them on the day only because discovery
            // happened to run past the arithmetic.
            var a = NewArbiter();
            a.Emit(Greeting, false, SpeechIntent.Queue, VerbosityLevel.Terse, "launch");
            a.Emit("Looking for radios on your network.", false, SpeechIntent.Queue,
                VerbosityLevel.Terse, "ProgressVoice");
            _clock.Advance(4000);
            a.Emit("Still looking for radios.", false, SpeechIntent.Queue,
                VerbosityLevel.Terse, "ProgressVoice");
            _clock.Advance(1000);
            a.Emit("Discovering radios", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "dialog");

            var rescued = _calls.Where(c => c.Salvaged).Select(c => c.Message).ToList();
            Assert.Contains("Looking for radios on your network.", rescued);
            Assert.Contains("Still looking for radios.", rescued);
        }

        [Fact]
        public void Interrupt_WithTheSameSubject_IsItselfTheSuperseder()
        {
            // The newest statement about a subject may be the interrupter
            // itself — "PC audio stopped because of an internal error" over a
            // "PC audio on." still queued. The queued line must not be
            // rescued behind the very line that retired it; the unrelated
            // summary still is.
            var a = NewArbiter();
            a.Emit(ConnectSummary, false, SpeechIntent.Queue, VerbosityLevel.Terse, "connect");
            a.Emit("PC audio on.", false, SpeechIntent.Queue, VerbosityLevel.Terse, "MainWindow",
                subject: SpeechSubject.PcAudio);
            _clock.Advance(500);
            a.Emit("PC audio stopped because of an internal error.", true, SpeechIntent.Interrupt,
                VerbosityLevel.Critical, "FlexBase", subject: SpeechSubject.PcAudio);

            Assert.DoesNotContain(_calls, c => c.Salvaged && c.Message == "PC audio on.");
            Assert.Contains(_calls, c => c.Salvaged && c.Message == ConnectSummary);
        }

        [Fact]
        public void LatestSweep_SupersedesTheFieldsQueuedCommittedValue()
        {
            // A Latest coalesce key is a subject by definition. The committed
            // "TX Power 5" is still queued when the operator starts sweeping
            // the same field; the sweep's lead is the newer statement, and
            // the committed value is not rescued behind it.
            string field = SpeechSubject.ValueField("TX Power");
            var a = NewArbiter();
            a.Emit(ConnectSummary, false, SpeechIntent.Queue, VerbosityLevel.Terse, "connect");
            a.Emit("TX Power 5", false, SpeechIntent.Queue, VerbosityLevel.Terse, "field",
                subject: field);
            _clock.Advance(500);
            a.Latest(field, "TX Power 6", VerbosityLevel.Terse, SpeechCoalesceKind.Value, "field");

            Assert.DoesNotContain(_calls, c => c.Salvaged && c.Message == "TX Power 5");
            Assert.Contains(_calls, c => c.Salvaged && c.Message == ConnectSummary);
        }

        [Fact]
        public void Supersession_ByAnEmissionTheReaderNeverGot_DoesNotCount()
        {
            // A superseder that was suppressed, or found no backend, covered
            // nothing anyone could hear. The earlier line is still the newest
            // thing the reader was actually given, and is still rescued.
            var a = NewArbiter();
            a.Emit(ConnectSummary, false, SpeechIntent.Queue, VerbosityLevel.Terse, "connect");
            a.Emit("PC audio on.", false, SpeechIntent.Queue, VerbosityLevel.Terse, "MainWindow",
                subject: SpeechSubject.PcAudio);
            _clock.Advance(100);
            _sinkResult = false;
            a.Emit("PC audio off", false, SpeechIntent.Queue, VerbosityLevel.Terse, "KeyCommands",
                subject: SpeechSubject.PcAudio);
            _sinkResult = true;
            _clock.Advance(100);
            a.Emit("Band changed", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t");

            Assert.Contains(_calls, c => c.Salvaged && c.Message == "PC audio on.");
        }

        [Fact]
        public void Salvage_ProvisionalReceipt_TheNewestCoversTheOlder()
        {
            // #442 seen from the ledger. Two changes in quick succession each
            // queue the receipt; an interrupt then rescues ONE reminder — the
            // newer — instead of repeating the sentence twice.
            const string receipt = "Changes to the radio will not survive disconnect unless you save the profile.";
            var a = NewArbiter();
            a.Emit(receipt, false, SpeechIntent.Queue, VerbosityLevel.Terse, "FlexBase",
                subject: SpeechSubject.ProvisionalReceipt);
            _clock.Advance(1000);
            a.Emit(receipt, false, SpeechIntent.Queue, VerbosityLevel.Terse, "FlexBase",
                subject: SpeechSubject.ProvisionalReceipt);
            _clock.Advance(500);
            a.Emit("Slice B released, 2 active", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t");

            Assert.Equal(1, _calls.Count(c => c.Salvaged && c.Message == receipt));
        }

        [Fact]
        public void Salvage_KeyedEntry_StillDiesAtTheCeiling()
        {
            // Supersession alone would let a subject nobody revisits live
            // forever. The mic-profile paragraph ahead of it keeps "PC audio
            // on." believed pending past fifteen seconds; at 14,999 ms it is
            // rescued, at 15,001 it is not — keyed or not, because the
            // ceiling is absolute. The paragraph, unkeyed, is refused at
            // 15,001 as well: under the old policy its bound was thirty
            // seconds, which is the other end of the inversion.
            var early = NewArbiter();
            early.Emit(MicProfileHeadsUp, false, SpeechIntent.Queue, VerbosityLevel.Terse, "connect");
            early.Emit("PC audio on.", false, SpeechIntent.Queue, VerbosityLevel.Terse, "MainWindow",
                subject: SpeechSubject.PcAudio);
            _clock.Advance(SpeechArbiter.SalvageCeilingMs - 1);
            early.Emit("Band changed", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t");
            Assert.Contains(_calls, c => c.Salvaged && c.Message == "PC audio on.");
            Assert.Contains(_calls, c => c.Salvaged && c.Message == MicProfileHeadsUp);

            _calls.Clear();
            var late = NewArbiter();
            late.Emit(MicProfileHeadsUp, false, SpeechIntent.Queue, VerbosityLevel.Terse, "connect");
            late.Emit("PC audio on.", false, SpeechIntent.Queue, VerbosityLevel.Terse, "MainWindow",
                subject: SpeechSubject.PcAudio);
            _clock.Advance(SpeechArbiter.SalvageCeilingMs + 1);
            late.Emit("Band changed", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t");
            Assert.DoesNotContain(_calls, c => c.Salvaged);
        }

        [Fact]
        public void TheCeilingIsTheEstimateCap_AndBothAreFifteenSeconds()
        {
            // Pinned, with the equality: the longest anything is estimated to
            // take to say is also the longest anything may wait to be said
            // about "now". Moving one without the other needs a reason here.
            Assert.Equal(15000, SpeechArbiter.SalvageCeilingMs);
            Assert.Equal(SpeechArbiter.SalvageCapMs, SpeechArbiter.SalvageCeilingMs);
        }
    }
}
