using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Test classes that drive the process-wide speech statics —
    /// <c>ScreenReaderOutput</c>'s coalescer state and verbosity, and
    /// <c>OutputChannelRecorder</c>'s global configuration.
    ///
    /// <para>Same mechanism, same reason as
    /// <see cref="RadioConfigStaticsCollection"/>: xUnit runs test classes in
    /// parallel, and two classes each calling
    /// <c>OutputChannelRecorder.Configure</c> would repoint the one global
    /// transcript out from under each other mid-test.
    /// OutputChannelRecorderTests carried this constraint the way
    /// KnownRadioRosterTests once did — "all tests live in this one class" —
    /// a rule that holds only until somebody adds a second class. Sprint 35
    /// Track M added one, so the comment becomes a mechanism now rather than
    /// after the predicted failure.</para>
    /// </summary>
    [CollectionDefinition(Name)]
    public sealed class SpeechOutputStaticsCollection
    {
        public const string Name = "Speech output statics";
    }

    // ────────────────────────────────────────────────────────────────
    //  Timing behaviour of the Latest-intent coalescer (Sprint 35 Track M).
    //
    //  These tests run against WALL CLOCK, because that is all the coalescer
    //  offers: its constants (CoalesceMs, SweepWindowMs, and the derived
    //  anti-clip gap) feed System.Threading.Timer and DateTime.UtcNow
    //  directly, so lead, settle and push-out cannot be exercised without
    //  actually waiting. That gap is
    //  itself a Track M finding, handed to Track L as a design: inject a
    //  clock and timer factory into ScreenReaderOutput and these tests can be
    //  rewritten on virtual time, exact and instant. Until then, every sleep
    //  below carries a margin of at least 3x against the constant it races
    //  (presses at 100 ms against a 300 ms timer), and no assertion depends
    //  on a sleep being accurate — only on it being shorter or longer than a
    //  constant by that margin.
    //
    //  Why these are worth their ~12 seconds: on 2026-08-26 Don reported
    //  Ctrl+S "just lags" when pressed repeatedly, and the mechanism was the
    //  coalescer's push-out — each press within the sweep window restarts the
    //  300 ms settle timer, so a hammered key is silent until released. That
    //  is CORRECT for a swept value (the settle policy is documented and
    //  deliberate) and wrong for an on-demand query key, which is what
    //  repeatWhileHeld exists to express. These tests pin both halves: the
    //  sweep contract stays as designed, and the repeatWhileHeld contract
    //  actually delivers what its comment promises.
    //
    //  Ctrl+S is the one production caller that passes the flag, and as of
    //  2026-08-27 it is still the only one — verified across every
    //  coalesceKey site. The other four (gain, volume, slice volume, value
    //  field) are SWEPT values, where the settle policy is right and the
    //  flag would reintroduce the periodic chatter removed on 2026-08-18.
    //  ValueFieldControl says so at its own call site and answers the
    //  end-of-range case with a tone instead.
    //
    //  Each test uses its own coalesce key and a GUID message prefix, and
    //  filters the transcript on that prefix — so a straggler timer from an
    //  earlier test flushing into a later test's transcript is invisible, and
    //  no test needs to reset coalescer state it cannot reach.
    // ────────────────────────────────────────────────────────────────
    [Collection(SpeechOutputStaticsCollection.Name)]
    public class SpeechCoalescerTimingTests : IDisposable
    {
        private readonly string _path;
        private readonly string _prefix;

        // Mirrors of the arbiter's constants. If they change, these tests fail
        // honestly (a settle arriving early or late) rather than silently
        // testing stale timing.
        //
        // GapCeilingMs replaced a flat MinGapMs on 2026-08-27 (#282): the
        // anti-clip gap is now derived per message and this is its upper
        // bound, so it remains the right worst-case drain margin below —
        // every real gap is this or shorter.
        private const int CoalesceMs = 300;
        private const int SweepWindowMs = 1200;
        private const int GapCeilingMs = 1200;

        public SpeechCoalescerTimingTests()
        {
            string dir = Path.Combine(Path.GetTempPath(), "jjflex-coalescer-tests");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".jsonl");
            _prefix = Guid.NewGuid().ToString("N").Substring(0, 8) + " ";

            OutputChannelRecorder.Configure(render: false, record: true, explicitPath: _path);
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
        }

        public void Dispose()
        {
            OutputChannelRecorder.Configure(render: false, record: false);
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
            try { File.Delete(_path); } catch { }
        }

        /// <summary>Speech texts recorded so far that belong to THIS test.</summary>
        private List<string> SpokenTexts()
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => JsonDocument.Parse(l))
                .Where(d => d.RootElement.GetProperty("event").GetString() == "speech")
                .Select(d => d.RootElement.GetProperty("text").GetString() ?? "")
                .Where(t => t.StartsWith(_prefix, StringComparison.Ordinal))
                .ToList();
        }

        private void Press(string key, string text, bool repeatWhileHeld = false)
        {
            ScreenReaderOutput.Speak(
                _prefix + text,
                Speech.SpeechIntent.Latest,
                VerbosityLevel.Terse,
                coalesceKey: key,
                repeatWhileHeld: repeatWhileHeld);
        }

        [Fact]
        public void DeliberatePresses_OutsideSweepWindow_EachSpeakImmediately()
        {
            // The cheap confirmation from the Track M brief, in executable
            // form: presses spaced wider than the sweep window never enter
            // the pending path — the lead Emit happens synchronously inside
            // Speak, so the transcript line exists before Speak returns.
            // No timer, no sleep-dependent assertion.
            string key = "test:" + _prefix + "slow";

            Press(key, "S9");
            Assert.Single(SpokenTexts());

            Thread.Sleep(SweepWindowMs + 400); // comfortably past the window

            Press(key, "S9 plus 10");
            Assert.Equal(2, SpokenTexts().Count);
        }

        [Fact]
        public void HammeredSweepKey_PushOutStarves_ThenSettlesOnFinalValue()
        {
            // The sweep contract, as designed and documented: a hammered key
            // without repeatWhileHeld speaks its lead, then every further
            // press pushes the settle timer out, so the hold is SILENT and
            // exactly one settle lands after release, carrying the final
            // value. For a swept gain this is the right policy. Applied to an
            // on-demand query key it is the "Ctrl+S just lags" report — which
            // is why this test exists: it is the diagnosis, executable.
            string key = "test:" + _prefix + "sweep";

            // 16 presses at ~100 ms — the press interval races the 300 ms
            // settle timer with a 3x margin, so a scheduling hiccup does not
            // let the timer fire mid-hammer.
            for (int i = 1; i <= 16; i++)
            {
                Press(key, "value " + i);
                Thread.Sleep(100);
            }

            // Read IMMEDIATELY after the last press, before its 300 ms timer
            // can fire: the lead must be the only thing spoken so far.
            var during = SpokenTexts();
            Assert.Single(during);
            Assert.EndsWith("value 1", during[0], StringComparison.Ordinal);

            // Drain: settle timer (300 ms) plus a full gap at its ceiling,
            // in case the
            // flush has to wait out the gap, plus margin.
            Thread.Sleep(CoalesceMs + GapCeilingMs + 500);

            var after = SpokenTexts();
            Assert.Equal(2, after.Count);
            Assert.EndsWith("value 16", after[1], StringComparison.Ordinal);
            // Nothing in between ever sounded — values 2 through 15 were
            // coalesced away. That is the sweep policy's promise.
        }

        [Fact]
        public void HammeredQueryKey_WithRepeatWhileHeld_SpeaksAtCadenceAndLandsOnFinalValue()
        {
            // The repeatWhileHeld contract: new presses update the pending
            // value but do NOT push the timer out, so a hammered key speaks
            // its lead and then a fresh reading roughly every gap, and the
            // final value always lands. This is what the Ctrl+S call site now
            // passes (Sprint 35 Track M) — a meter query where "still S9" is
            // information and silence-until-release is a bug.
            string key = "test:" + _prefix + "query";

            // Hammer for ~2.6 s: lead at ~0, first cadence utterance at
            // ~one gap (1200 ms at the ceiling), second at ~two.
            for (int i = 1; i <= 26; i++)
            {
                Press(key, "reading " + i, repeatWhileHeld: true);
                Thread.Sleep(100);
            }

            // Read right after the last press: the lead plus at least one
            // mid-hammer cadence utterance must already be out. (Expected is
            // three by now — asserting two leaves margin for late timers.)
            var during = SpokenTexts();
            Assert.True(during.Count >= 2,
                $"expected the lead plus at least one cadence utterance during the hammer, got {during.Count}");
            Assert.EndsWith("reading 1", during[0], StringComparison.Ordinal);

            // Drain and confirm the final reading was spoken last.
            Thread.Sleep(CoalesceMs + GapCeilingMs + 500);
            var after = SpokenTexts();
            Assert.EndsWith("reading 26", after[after.Count - 1], StringComparison.Ordinal);
        }

        [Fact]
        public void RepeatedIdenticalValue_WithoutFlag_SecondPressIsSwallowed()
        {
            // The duplicate drop, as designed for sweeps: a settle that would
            // repeat what was just said is skipped. For a query key this is
            // the other half of the Ctrl+S report — press twice on a steady
            // signal and the second press says nothing at all.
            string key = "test:" + _prefix + "dup";

            Press(key, "S7");
            Thread.Sleep(800); // inside the sweep window: second press coalesces
            Press(key, "S7");

            Thread.Sleep(CoalesceMs + GapCeilingMs + 500);
            Assert.Single(SpokenTexts());
        }

        [Fact]
        public void RepeatedIdenticalValue_WithFlag_SecondPressIsSpoken()
        {
            // With repeatWhileHeld the repetition IS the information —
            // "still S7" is how the operator learns the signal is steady.
            string key = "test:" + _prefix + "dup-flag";

            Press(key, "S7", repeatWhileHeld: true);
            Thread.Sleep(800);
            Press(key, "S7", repeatWhileHeld: true);

            Thread.Sleep(CoalesceMs + GapCeilingMs + 500);
            Assert.Equal(2, SpokenTexts().Count);
        }
    }
}
