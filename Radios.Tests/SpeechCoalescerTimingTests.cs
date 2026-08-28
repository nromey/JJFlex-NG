#nullable enable
using System;
using System.Collections.Generic;
using Radios;
using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    // ────────────────────────────────────────────────────────────────
    //  Coalescer timing under a HELD or HAMMERED key — the multi-press
    //  behaviours, as opposed to SpeechArbiterTests' boundary arithmetic.
    //
    //  PORTED TO THE INJECTED CLOCK 2026-08-27 (#285). This file ran on the
    //  wall clock and took about twelve seconds, and its own header said it
    //  should be rewritten once the clock seam existed. It was the last speech
    //  test that slept; every neighbour in this directory had already moved.
    //
    //  Why that mattered more than the twelve seconds. A wall-clock timing test
    //  passes or fails partly on what else the machine was doing, which makes
    //  it the natural candidate to become a test that fails only in the full
    //  suite and teaches people to re-run rather than look — an instrument that
    //  lies occasionally, in a sprint about instruments that lie. The sleeps
    //  also could not assert what these tests actually claim: "silent until
    //  released" was asserted by reading the transcript quickly enough, which
    //  is a race dressed as an assertion. On virtual time it is a fact — advance
    //  to one millisecond before the flush, assert silence, advance one more.
    //
    //  The port also drops the transcript plumbing. These tests asserted
    //  through a recorded JSONL file and a GUID message prefix, because they
    //  drove the process-wide statics and could not reset what they could not
    //  reach. Driving the arbiter directly, each test owns its own instance and
    //  its own clock, so the collection serialisation, the temp files and the
    //  prefix filtering are all gone with the sleeps.
    //
    //  WHAT THEY PIN, unchanged in meaning by the port:
    //  a swept value starves under a hammer and settles once on the final
    //  value, and a QUERY key answers instead — which is #264's rule seen from
    //  the hammering end. Ctrl+S is the one production Query call site.
    // ────────────────────────────────────────────────────────────────
    public class SpeechCoalescerTimingTests
    {
        private readonly FakeSpeechClock _clock = new();
        private readonly List<(string Message, double AtMs)> _spoken = new();
        private VerbosityLevel _verbosity = VerbosityLevel.Chatty;

        private SpeechArbiter NewArbiter() => new SpeechArbiter(
            _clock,
            () => _verbosity,
            (message, interrupt, intent, level, origin, salvaged) =>
            {
                _spoken.Add((message, _clock.ElapsedMs));
                return true;
            },
            () => { },
            (message, level, intent, origin) => { });

        /// <summary>
        /// Press a key n times at <paramref name="everyMs"/> intervals, texts
        /// numbered from 1. Mirrors a real key repeat: press, then let time
        /// pass, so a timer coming due between presses fires between them.
        /// </summary>
        private void Hammer(SpeechArbiter a, string key, string stem,
            SpeechCoalesceKind kind, int presses, int everyMs)
        {
            for (int i = 1; i <= presses; i++)
            {
                a.Latest(key, $"{stem} {i}", VerbosityLevel.Terse, kind, "t");
                _clock.Advance(everyMs);
            }
        }

        [Fact]
        public void DeliberatePresses_OutsideSweepWindow_EachSpeakImmediately()
        {
            // Presses spaced wider than the sweep window never enter the
            // pending path at all: the lead emits synchronously inside Latest,
            // so the utterance exists before the call returns. No timer.
            var a = NewArbiter();

            a.Latest("slow", "S9", VerbosityLevel.Terse, SpeechCoalesceKind.Value, "t");
            Assert.Single(_spoken);

            // Past BOTH the 1200 ms sweep window and the gap "S9" earned.
            _clock.Advance(SpeechArbiter.SweepWindowMs + 400);

            a.Latest("slow", "S9 plus 10", VerbosityLevel.Terse, SpeechCoalesceKind.Value, "t");
            Assert.Equal(2, _spoken.Count);
            Assert.Equal(1600, _spoken[1].AtMs);
        }

        [Fact]
        public void HammeredSweepKey_PushOutStarves_ThenSettlesOnFinalValue()
        {
            // The sweep contract, as designed and documented: a hammered VALUE
            // key speaks its lead, then every further press pushes the settle
            // timer out, so the hold is SILENT and exactly one settle lands
            // after release carrying the final value. For a swept gain this is
            // right — it is what stops a hold being heard as clicks and ticks.
            //
            // Applied to an on-demand query key it is Don's "I hit ctrl+s and
            // it just lags", which is why this test sits beside the query one:
            // the two are the same mechanism meeting opposite intents.
            var a = NewArbiter();

            // "value 1" is seven characters, so the lead's anti-clip gap is
            // 770 ms — irrelevant here, because the push-out carries the flush
            // far past it.
            Assert.Equal(770, SpeechArbiter.AntiClipGapMs("value 1"));

            // 16 presses at 100 ms. Each one restarts the 300 ms settle, so the
            // flush never comes due while a finger is still moving.
            Hammer(a, "sweep", "value", SpeechCoalesceKind.Value, presses: 16, everyMs: 100);

            // t = 1600, immediately after the last press: the lead is still the
            // only thing spoken. On the wall clock this was a race against the
            // settle timer; here it is a fact.
            Assert.Single(_spoken);
            Assert.Equal("value 1", _spoken[0].Message);

            // Release. The last press was at 1500, so the settle is due at
            // 1800 and the gap is long gone.
            _clock.Advance(199);            // t = 1799
            Assert.Single(_spoken);
            _clock.Advance(1);              // t = 1800
            Assert.Equal(2, _spoken.Count);
            Assert.Equal("value 16", _spoken[1].Message);
            Assert.Equal(1800, _spoken[1].AtMs);

            // Values 2 through 15 were coalesced away and never sounded. That
            // is the sweep policy's promise, and the reason it is wrong for a
            // key that asks a question.
            Assert.DoesNotContain(_spoken, s => s.Message == "value 8");
        }

        [Fact]
        public void HammeredQueryKey_SpeaksAtTheGapCadence_AndLandsOnTheFinalReading()
        {
            // The query contract under a hammer: presses update the pending
            // reading but never push the flush out, so the key answers at the
            // anti-clip cadence instead of going silent — and the final reading
            // always lands.
            //
            // The cadence is the GAP, not the settle: "reading 1" is nine
            // characters (990 ms) and "reading 10" is ten (1100 ms), so each
            // utterance waits only as long as the previous one needs to finish.
            // That spacing is exactly why Query does not bypass the gap: it is
            // what makes a held query readable rather than a stutter.
            var a = NewArbiter();
            Assert.Equal(990, SpeechArbiter.AntiClipGapMs("reading 1"));
            Assert.Equal(1100, SpeechArbiter.AntiClipGapMs("reading 10"));

            Hammer(a, "query", "reading", SpeechCoalesceKind.Query, presses: 26, everyMs: 100);

            // t = 2600. Three utterances are already out — the lead at 0, then
            // one per gap — while a swept key would still be silent.
            Assert.Equal(3, _spoken.Count);
            Assert.Equal(("reading 1", 0d), _spoken[0]);
            Assert.Equal(("reading 10", 990d), _spoken[1]);
            Assert.Equal(("reading 21", 2090d), _spoken[2]);

            // Release: the final reading lands one gap after the last
            // utterance, and it is the newest one, not a stale mid-hammer value.
            _clock.Advance(589);            // t = 3189
            Assert.Equal(3, _spoken.Count);
            _clock.Advance(1);              // t = 3190
            Assert.Equal(4, _spoken.Count);
            Assert.Equal("reading 26", _spoken[3].Message);
        }

        [Fact]
        public void RepeatedIdenticalValue_OnASweptKey_SecondPressIsSwallowed()
        {
            // The duplicate drop, as designed for sweeps: a settle that would
            // only repeat what the lead already said is skipped, because
            // repeating it cuts the lead off to say nothing new.
            var a = NewArbiter();

            a.Latest("dup", "S7", VerbosityLevel.Terse, SpeechCoalesceKind.Value, "t");
            _clock.Advance(800);            // inside the sweep window
            a.Latest("dup", "S7", VerbosityLevel.Terse, SpeechCoalesceKind.Value, "t");

            _clock.Advance(10_000);
            Assert.Single(_spoken);
        }

        [Fact]
        public void RepeatedIdenticalValue_OnAQueryKey_SecondPressIsSpokenAtOnce()
        {
            // The same two presses on a query key, and the whole of #264 in one
            // assertion. The operator asked twice and must be answered twice —
            // "still S7" is how a steady signal is reported — and the answer
            // arrives at the moment of the press, not a settle later.
            var a = NewArbiter();

            a.Latest("dup", "S7", VerbosityLevel.Terse, SpeechCoalesceKind.Query, "t");
            _clock.Advance(800);            // past the 700 ms gap "S7" earned
            a.Latest("dup", "S7", VerbosityLevel.Terse, SpeechCoalesceKind.Query, "t");

            Assert.Equal(2, _spoken.Count);
            Assert.Equal("S7", _spoken[1].Message);
            Assert.Equal(800, _spoken[1].AtMs);
        }
    }
}
