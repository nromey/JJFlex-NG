#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using JJTrace;
using Radios;
using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    // ────────────────────────────────────────────────────────────────
    //  The salvage settle window (#507) — Sprint 44 Track H.
    //
    //  An interrupt's salvage train used to be handed to the reader inside
    //  the interrupt's own call, which put it immediately behind the
    //  interrupter. The interrupter is the LEAD of an action whose real
    //  answer is queued a moment later by the same handler, so the operator
    //  heard the lead, then the backlog, then the answer — "Tune on", a
    //  sixty-character warning about profile saving, and only then the
    //  radio acting (JJFlexRadioTrace-20260901-202636.txt @65323). And a
    //  burst of presses re-rescued the same backlog on every press, each
    //  press spending one of its two rescues (213210 @4015266, @4015770,
    //  @4016234: capped on the third Tab).
    //
    //  The train is now HELD for SpeechArbiter.SalvageSettleMs after the
    //  last interrupt, anything queued meanwhile goes to the reader at once,
    //  and the release hands the train over behind it. The tests below are
    //  the acceptance cases from the track brief, replayed at the traces'
    //  own offsets on the injected clock, plus the invariants that make the
    //  window safe: it delays NOTHING the operator is waiting for, it
    //  swallows nothing, and it is bracketed by the measurements it was
    //  derived from.
    //
    //  What the operator hears for the tune case, in order —
    //    before: "Tune on" · the profile warning · "Tune Power 5" · (the
    //            radio had keyed 13 ms after "Tune on", under all of it)
    //    after:  "Tune on" · (radio keys at +13 ms) · 600 ms · the profile
    //            warning · "Tune Power 5"
    //  and for the tune's END, which is where the SWR reading lives —
    //    before: "Tune off" · the backlog · "SWR 1.7"
    //    after:  "Tune off" · "SWR 1.7" · 600 ms · the backlog
    // ────────────────────────────────────────────────────────────────
    public class SalvageSettleWindowTests
    {
        private sealed record SinkCall(string Message, bool Interrupt, bool Salvaged, double AtMs);

        private readonly FakeSpeechClock _clock = new();
        private readonly List<SinkCall> _calls = new();
        private bool _sinkResult = true;
        private int _silenceCount;

        private const int Settle = SpeechArbiter.SalvageSettleMs;

        private SpeechArbiter NewArbiter() => new SpeechArbiter(
            _clock,
            () => VerbosityLevel.Chatty,
            (message, interrupt, intent, level, origin, salvaged) =>
            {
                _calls.Add(new SinkCall(message, interrupt, salvaged, _clock.ElapsedMs));
                return _sinkResult;
            },
            () => _silenceCount++,
            (message, level, intent, origin) => { });

        private IEnumerable<SinkCall> Salvaged => _calls.Where(c => c.Salvaged);

        /// <summary>The receipt as it read on 2026-09-01, 78 characters.</summary>
        private const string Receipt =
            "Changes to the radio will not survive disconnect unless you save the profile.";

        // ── The window is bracketed by the measurements it came from ──

        [Fact]
        public void TheWindow_IsBracketedByTheMeasurements()
        {
            // Below: the slowest same-action follow-up in the 2026-09-01
            // Verbose captures — "PC audio on" 264 ms behind the Home
            // arrival — and the slowest press of a deliberate burst, 552 ms
            // between two Tabs. Above: GapFloorMs, the arbiter's own floor on
            // how long the reader is busy with its shortest utterance;
            // releasing inside it means the reader has not run out of things
            // to say, so the hold is inaudible by the estimate the anti-clip
            // gap already relies on. Move the constant outside this bracket
            // and one of those three facts has to be re-measured first.
            Assert.True(SpeechArbiter.SalvageSettleMs > 264,
                "the window must outlast the slowest measured same-action follow-up (264 ms)");
            Assert.True(SpeechArbiter.SalvageSettleMs > 552,
                "the window must outlast the slowest measured press of a Tab burst (552 ms), or a burst spends a rescue per press again");
            Assert.True(SpeechArbiter.SalvageSettleMs < SpeechArbiter.GapFloorMs,
                "the window must close before the reader could have finished its shortest utterance, or the hold becomes an audible pause");
        }

        // ── The 2026-09-01 tune tick, both halves ──

        [Fact]
        public void TuneTick_202636_TheReceiptLandsAfterTheRadioActs_NotBetween()
        {
            // 202636 @65322–65336, at its own offsets. The receipt had been
            // queued 7.6 s earlier and rescued once already; "5" had been
            // typed into Tune Power and the entry had ended as "Tune Power 5";
            // then Tune, and 13 ms later mainLoop:TXTune. Under the old
            // contract the receipt and "Tune Power 5" were handed over in
            // the Tune keypress itself, so speech was still on the warning
            // when the radio keyed.
            string entry = SpeechSubject.ValueEntry("Tune Power");
            string field = SpeechSubject.ValueField("Tune Power");
            var a = NewArbiter();

            a.Emit(Receipt, false, SpeechIntent.Queue, VerbosityLevel.Terse, "FlexBase",
                subject: SpeechSubject.ProvisionalReceipt);

            // The field entry, 2,247 ms before the tune: its lead rescues
            // the receipt (its first rescue), the digit and the committed
            // value are queued inside that window and go straight through.
            _clock.Advance(5353);
            a.Emit("Enter Tune Power value", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "field");
            _clock.Advance(1);
            a.Emit("5", false, SpeechIntent.Queue, VerbosityLevel.Critical, "field",
                subject: entry, additive: true);
            _clock.Advance(480);
            a.Supersede(entry, "the entry ending as 'Tune Power 5'", "field");
            a.Emit("Tune Power 5", false, SpeechIntent.Queue, VerbosityLevel.Terse, "field",
                subject: field);
            _clock.Advance(7600 - 5834);

            // Tune. The radio acts 13 ms later.
            int tuneAt = (int)_clock.ElapsedMs;
            a.Emit("Tune on", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "MainWindow");
            Assert.Equal("Tune on", _calls[^1].Message);
            int countAfterLead = _calls.Count;

            _clock.Advance(13);                       // mainLoop:TXTune
            Assert.Equal(countAfterLead, _calls.Count);
            _clock.Advance(Settle - 13 - 1);           // one short of the window
            Assert.Equal(countAfterLead, _calls.Count);
            _clock.Advance(1);                         // the window closes

            // The value first, then the receipt: the receipt's first rescue
            // re-entered the ledger when ITS window closed, behind the value
            // the operator had typed meanwhile — which is the order the
            // reader actually had them in. On the day the receipt came first
            // only because that rescue was handed over inside the field
            // entry's own keypress, ahead of the digit.
            var train = _calls.Skip(countAfterLead).ToList();
            Assert.Equal(new[] { "Tune Power 5", Receipt }, train.Select(c => c.Message));
            Assert.All(train, c =>
            {
                Assert.True(c.Salvaged);
                Assert.Equal(tuneAt + Settle, c.AtMs);
            });

            // The digit was covered by the entry ending and is never rescued,
            // by either interrupt.
            Assert.DoesNotContain(Salvaged, c => c.Message == "5");
        }

        [Fact]
        public void TuneOff_TheSwrReadingGoesAheadOfTheHeldTrain()
        {
            // The end of the tune is where #503's reading lives: "Tune off",
            // then "SWR 1.7" queued one millisecond later by the same
            // handler (202636 shows this seven times at +0 and +1 ms). The
            // reading must reach the reader at +1 ms — untouched by the
            // window — and the backlog must land behind it, not in front.
            var a = NewArbiter();
            a.Emit(Receipt, false, SpeechIntent.Queue, VerbosityLevel.Terse, "FlexBase",
                subject: SpeechSubject.ProvisionalReceipt);
            _clock.Advance(2000);
            a.Emit("Tune off", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "MainWindow");
            _clock.Advance(1);
            a.Emit("SWR 1.7", false, SpeechIntent.Queue, VerbosityLevel.Terse, "MainWindow",
                subject: SpeechSubject.SwrAfterTune);
            _clock.Advance(Settle);

            Assert.Equal(new[] { Receipt, "Tune off", "SWR 1.7", Receipt },
                _calls.Select(c => c.Message));
            Assert.Equal(2001, _calls[2].AtMs);
            Assert.False(_calls[2].Salvaged);
            Assert.Equal(2000 + Settle, _calls[3].AtMs);
            Assert.True(_calls[3].Salvaged);
        }

        // ── A burst spends one rescue, not one per press ──

        [Fact]
        public void ThreeTabs_InsideOneWindow_HandTheTrainOverOnce_AndSpendOneRescue()
        {
            // 213210 @4015266, @4015770, @4016234: Tab, Tab 504 ms later,
            // Tab 464 ms after that, with the slice census and the receipt
            // in the ledger. On the day: rescued on the first press, rescued
            // on the second, dropped at the cap on the third. Now: held
            // through all three, handed over once when the burst ends, and
            // a fourth press well afterwards finds them with one rescue each
            // and rescues them again.
            const string census = "1 slice out of 2 used, slice A USB";
            const string home = "JJ Flexible Home, slice, 14.100.000";
            var a = NewArbiter();
            a.Emit(census, false, SpeechIntent.Queue, VerbosityLevel.Terse, "FlexBase");
            a.Emit(Receipt, false, SpeechIntent.Queue, VerbosityLevel.Terse, "FlexBase",
                subject: SpeechSubject.ProvisionalReceipt);

            _clock.Advance(1000);
            a.Emit(home, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "Home");
            _clock.Advance(504);
            a.Emit(home, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "Home");
            _clock.Advance(464);
            a.Emit(home, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "Home");
            int lastTabAt = (int)_clock.ElapsedMs;

            // Nothing salvaged during the burst, everything once after it.
            Assert.Empty(Salvaged);
            _clock.Advance(Settle - 1);
            Assert.Empty(Salvaged);
            _clock.Advance(1);
            Assert.Equal(new[] { census, Receipt }, Salvaged.Select(c => c.Message));
            Assert.All(Salvaged, c => Assert.Equal(lastTabAt + Settle, c.AtMs));

            // A fourth Tab after the hand-over: both are still worth
            // rescuing — one rescue spent, not two — so both come back.
            _clock.Advance(900);
            a.Emit(home, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "Home");
            _clock.Advance(Settle);
            Assert.Equal(2, Salvaged.Count(c => c.Message == Receipt));
            Assert.Equal(2, Salvaged.Count(c => c.Message == census));
        }

        [Fact]
        public void ThreeTabs_SpacedPastTheWindow_TheControl_StillSpendARescueEach()
        {
            // The same three presses, each past the previous hand-over. This
            // is the old contract and it is unchanged: rescue, rescue, cap.
            // If the burst test above went green with this spacing too, the
            // window would not be what coalesced it.
            const string census = "1 slice out of 2 used, slice A USB";
            const string home = "JJ Flexible Home, slice, 14.100.000";
            var a = NewArbiter();
            a.Emit(census, false, SpeechIntent.Queue, VerbosityLevel.Terse, "FlexBase");
            a.Emit(Receipt, false, SpeechIntent.Queue, VerbosityLevel.Terse, "FlexBase",
                subject: SpeechSubject.ProvisionalReceipt);

            _clock.Advance(1000);
            a.Emit(home, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "Home");
            _clock.Advance(Settle + 100);
            a.Emit(home, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "Home");
            _clock.Advance(Settle + 100);
            a.Emit(home, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "Home");
            _clock.Advance(Settle);

            Assert.Equal(SpeechArbiter.MaxSalvages, Salvaged.Count(c => c.Message == Receipt));
            Assert.Equal(SpeechArbiter.MaxSalvages, Salvaged.Count(c => c.Message == census));
        }

        [Fact]
        public void ASecondInterruptInsideTheWindow_ReJudgesWhatIsHeld()
        {
            // "PC audio on." is held behind an unrelated interrupt; 300 ms
            // later "PC audio off" interrupts on the same subject. The held
            // entry is covered now, and the release must not hand it over —
            // the burst re-judges the held set, it does not merely re-arm.
            var a = NewArbiter();
            a.Emit("PC audio on.", false, SpeechIntent.Queue, VerbosityLevel.Terse, "MainWindow",
                subject: SpeechSubject.PcAudio);
            _clock.Advance(100);
            a.Emit("Band changed", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t");
            _clock.Advance(300);
            a.Emit("PC audio off", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "KeyCommands",
                subject: SpeechSubject.PcAudio);
            _clock.Advance(Settle);

            Assert.Empty(Salvaged);
        }

        // ── The window delays nothing the operator is waiting for ──

        [Fact]
        public void HeldArrowSweep_LeadAndSettleLandAtTheSameInstants_WithOrWithoutABacklog()
        {
            // The same sweep run twice: once with nothing in the ledger, once
            // with a backlog the lead has to rescue. The lead and the settle
            // are the operator's own speech and must land at identical
            // instants in both — the window moves only the backlog. With the
            // backlog, the train lands ONCE, after the sweep, with a single
            // rescue: the release sees a settle still pending and keeps
            // holding rather than handing the train over to be flushed by it.
            var quiet = RunSweep(withBacklog: false);
            var loaded = RunSweep(withBacklog: true);

            // The sweep is the Latest utterances — the lead and the settle,
            // both interrupting. The loaded run also carries the backlog's
            // own first emission at 0 and its rescue, which are not the sweep.
            var sweepAlone = quiet.Where(c => c.Interrupt).Select(c => (c.Message, c.AtMs)).ToList();
            var sweepLoaded = loaded.Where(c => c.Interrupt).Select(c => (c.Message, c.AtMs)).ToList();
            Assert.Equal(new[] { ("RF gain 5", 100d), ("RF gain 8", 1200d) }, sweepAlone);
            Assert.Equal(sweepAlone, sweepLoaded);
            Assert.DoesNotContain(quiet, c => c.Salvaged);

            // The backlog: exactly one hand-over, after the settle. The lead
            // opened the window at 100; the release at 700 found the sweep's
            // settle still pending and kept holding; the settle at 1200
            // re-armed it as any interrupt does; and the train landed at 1800.
            var only = Assert.Single(loaded.Where(c => c.Salvaged));
            Assert.Equal("Session closed", only.Message);
            Assert.Equal(1200 + Settle, only.AtMs);
        }

        private static List<SinkCall> RunSweep(bool withBacklog)
        {
            var clock = new FakeSpeechClock();
            var calls = new List<SinkCall>();
            var a = new SpeechArbiter(
                clock,
                () => VerbosityLevel.Chatty,
                (message, interrupt, intent, level, origin, salvaged) =>
                {
                    calls.Add(new SinkCall(message, interrupt, salvaged, clock.ElapsedMs));
                    return true;
                },
                () => { },
                (message, level, intent, origin) => { });

            if (withBacklog)
                a.Emit("Session closed", false, SpeechIntent.Queue, VerbosityLevel.Terse, "t");

            // Lead at 100, then a hold: values at 500, 700 and 900 each push
            // the settle out by CoalesceMs, so it comes due at 1200 — past
            // the 990 ms gap "RF gain 5" earned — and speaks then.
            clock.Advance(100);
            a.Latest("rf", "RF gain 5", VerbosityLevel.Terse, SpeechCoalesceKind.Value, "t");
            clock.Advance(400);
            a.Latest("rf", "RF gain 6", VerbosityLevel.Terse, SpeechCoalesceKind.Value, "t");
            clock.Advance(200);
            a.Latest("rf", "RF gain 7", VerbosityLevel.Terse, SpeechCoalesceKind.Value, "t");
            clock.Advance(200);
            a.Latest("rf", "RF gain 8", VerbosityLevel.Terse, SpeechCoalesceKind.Value, "t");
            clock.Advance(5000);
            return calls;
        }

        [Fact]
        public void AFollowUpInsideTheWindow_GoesToTheReaderAtOnce_AheadOfTheTrain()
        {
            // 213210 @4011617: "Slice A, first slice" interrupts with two
            // slice lines in the ledger; the census is queued 50 ms later
            // from a worker thread. The census goes at +50 — not a
            // millisecond later than it did — and the slice lines land
            // behind it when the window closes.
            var a = NewArbiter();
            a.Emit("Slice A, 14.100 USB, yours", false, SpeechIntent.Queue, VerbosityLevel.Terse, "FlexBase");
            a.Emit("Slice B, 14.100 USB, yours", false, SpeechIntent.Queue, VerbosityLevel.Terse, "FlexBase");
            _clock.Advance(500);
            a.Emit("Slice A, first slice", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t");
            _clock.Advance(50);
            a.Emit("1 slice out of 2 used, slice A USB", false, SpeechIntent.Queue, VerbosityLevel.Terse, "FlexBase");
            _clock.Advance(Settle - 50);

            Assert.Equal(
                new[]
                {
                    ("Slice A, 14.100 USB, yours", 0d), ("Slice B, 14.100 USB, yours", 0d),
                    ("Slice A, first slice", 500d),
                    ("1 slice out of 2 used, slice A USB", 550d),
                    ("Slice A, 14.100 USB, yours", 500d + Settle), ("Slice B, 14.100 USB, yours", 500d + Settle),
                },
                _calls.Select(c => (c.Message, c.AtMs)));
            Assert.False(_calls[3].Salvaged);
            Assert.True(_calls[4].Salvaged && _calls[5].Salvaged);
        }

        [Fact]
        public void AnInterruptWithNothingToHold_ArmsNothing()
        {
            // An empty ledger means no train and no window: the follow-up is
            // untouched, and nothing salvaged ever appears.
            var a = NewArbiter();
            a.Emit("Tune off", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "MainWindow");
            _clock.Advance(1);
            a.Emit("SWR 1.1", false, SpeechIntent.Queue, VerbosityLevel.Terse, "MainWindow",
                subject: SpeechSubject.SwrAfterTune);
            _clock.Advance(10_000);

            Assert.Equal(new[] { ("Tune off", 0d), ("SWR 1.1", 1d) }, _calls.Select(c => (c.Message, c.AtMs)));
            Assert.Empty(Salvaged);
        }

        // ── Supersession walks the held set ──

        [Fact]
        public void AQueuedFollowUpOnTheSameSubject_RetiresAHeldEntry()
        {
            // 213210 @4004072: "RIT off" interrupts, "XIT +0" is queued one
            // millisecond later. A held "XIT +100" on the same field is
            // covered by it and must not come back behind it.
            string xit = SpeechSubject.ValueField("XIT");
            var a = NewArbiter();
            a.Emit("XIT +100", false, SpeechIntent.Queue, VerbosityLevel.Terse, "FreqOut", subject: xit);
            _clock.Advance(100);
            a.Emit("RIT off", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "KeyCommands");
            _clock.Advance(1);
            a.Emit("XIT +0", false, SpeechIntent.Queue, VerbosityLevel.Terse, "FreqOut", subject: xit);
            _clock.Advance(Settle);

            Assert.Equal(new[] { "XIT +100", "RIT off", "XIT +0" }, _calls.Select(c => c.Message));
            Assert.Empty(Salvaged);
        }

        [Fact]
        public void SupersedeInWords_DuringTheWindow_RetiresAHeldEntry()
        {
            var a = NewArbiter();
            a.Emit("Still looking for radios.", false, SpeechIntent.Queue, VerbosityLevel.Terse,
                "ProgressVoice", subject: SpeechSubject.Progress);
            _clock.Advance(100);
            a.Emit("Discovering radios", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "dialog");
            _clock.Advance(50);
            a.Supersede(SpeechSubject.Progress, "the end of the wait", "ProgressVoice");
            _clock.Advance(Settle);

            Assert.Empty(Salvaged);
        }

        // ── The window swallows nothing: every exit is a hand-over or a stated refusal ──

        [Fact]
        public void OnSilenced_DuringTheWindow_LetsTheTrainGo()
        {
            var a = NewArbiter();
            a.Emit("Session closed", false, SpeechIntent.Queue, VerbosityLevel.Terse, "t");
            _clock.Advance(100);
            a.Emit("Band changed", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t");
            _clock.Advance(200);
            a.OnSilenced();
            _clock.Advance(Settle);

            Assert.Empty(Salvaged);
        }

        [Fact]
        public void Urgent_DuringTheWindow_DiscardsTheTrain()
        {
            var a = NewArbiter();
            a.Emit("Session closed", false, SpeechIntent.Queue, VerbosityLevel.Terse, "t");
            _clock.Advance(100);
            a.Emit("Band changed", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t");
            _clock.Advance(200);
            a.Urgent("80 percent of your power is coming back on ANT2.", VerbosityLevel.Critical, "t");
            _clock.Advance(Settle);

            Assert.Equal(1, _silenceCount);
            Assert.Empty(Salvaged);
        }

        [Fact]
        public void DiscardAll_DuringTheWindow_DropsTheTrain()
        {
            var a = NewArbiter();
            a.Emit("Session closed", false, SpeechIntent.Queue, VerbosityLevel.Terse, "t");
            _clock.Advance(100);
            a.Emit("Band changed", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t");
            _clock.Advance(200);
            a.DiscardAll();
            _clock.Advance(Settle);

            Assert.Empty(Salvaged);
        }

        [Fact]
        public void AReaderThatRefusesAtTheRelease_IsNotReEntered()
        {
            // The backend went away while the train waited. The hand-over is
            // attempted and refused; the entry occupies nothing and re-enters
            // nothing, so a later interrupt has nothing to rescue.
            var a = NewArbiter();
            a.Emit("Session closed", false, SpeechIntent.Queue, VerbosityLevel.Terse, "t");
            _clock.Advance(100);
            a.Emit("Band changed", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t");
            _sinkResult = false;
            _clock.Advance(Settle);
            Assert.Single(Salvaged);                  // offered, and refused

            _sinkResult = true;
            _clock.Advance(100);
            a.Emit("Mode changed", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t");
            _clock.Advance(Settle);
            Assert.Single(Salvaged);                  // nothing came back a second time
        }

        /// <summary>The 300-character courtesy from 2026-09-01: long enough to keep what is queued behind it believed pending for the full ceiling.</summary>
        private const string MicProfileHeadsUp =
            "Heads up: this radio has no mic profile selected. Until one is loaded, audio from "
            + "your computer will not be transmitted through your radio — you would key up and "
            + "nobody would hear you. Nothing you did caused it, and receive is unaffected. The "
            + "Audio Workshop has the details.";

        [Fact]
        public void ABurstThatCarriesAnEntryAcrossTheCeiling_RefusesItAtTheReJudge()
        {
            // Held from 14.4 s behind a single press, the entry is handed over
            // at exactly the ceiling and rescued — the control. Held from the
            // same instant and re-armed by a press every half second, it is
            // past the fifteen-second ceiling by the third press and the
            // re-judge refuses it there: it never reaches the reader, and the
            // refusal is traced at the press that found it, not lost in the
            // hold.
            var alone = NewArbiter();
            alone.Emit(MicProfileHeadsUp, false, SpeechIntent.Queue, VerbosityLevel.Terse, "connect");
            alone.Emit("PC audio on.", false, SpeechIntent.Queue, VerbosityLevel.Terse, "MainWindow",
                subject: SpeechSubject.PcAudio);
            _clock.Advance(SpeechArbiter.SalvageCeilingMs - Settle);       // 14,400
            alone.Emit("Tab one", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t");
            _clock.Advance(Settle);                                           // 15,000: at the ceiling, not past it
            Assert.Contains(Salvaged, c => c.Message == "PC audio on.");

            _calls.Clear();
            var burst = NewArbiter();
            burst.Emit(MicProfileHeadsUp, false, SpeechIntent.Queue, VerbosityLevel.Terse, "connect");
            burst.Emit("PC audio on.", false, SpeechIntent.Queue, VerbosityLevel.Terse, "MainWindow",
                subject: SpeechSubject.PcAudio);
            _clock.Advance(SpeechArbiter.SalvageCeilingMs - Settle);       // +14,400
            burst.Emit("Tab one", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t");
            _clock.Advance(500);                                              // +14,900
            burst.Emit("Tab two", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t");
            _clock.Advance(500);                                              // +15,400
            burst.Emit("Tab three", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t");
            _clock.Advance(Settle);

            Assert.Empty(Salvaged);
        }

        // ── The trace says a hold happened, how long, and what went first ──
        //
        //  Silence with no trace line is the original sin here, so the record
        //  is pinned as content, not as a promise: these lines come out of
        //  JJTrace itself, from the same code the radio runs.

        [Fact]
        public void TheTraceSaysAHoldHappened_HowLong_AndWhatWentFirst()
        {
            const string census = "1 slice out of 2 used, slice A USB";
            const string home = "JJ Flexible Home, slice, 14.100.000";
            var captured = CaptureTrace(() =>
            {
                var a = NewArbiter();
                a.Emit("Slice A, 14.100 USB, yours", false, SpeechIntent.Queue, VerbosityLevel.Terse, "FlexBase");
                a.Emit(Receipt, false, SpeechIntent.Queue, VerbosityLevel.Terse, "FlexBase",
                    subject: SpeechSubject.ProvisionalReceipt);
                _clock.Advance(1000);
                a.Emit(home, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "Home");   // the hold opens
                _clock.Advance(50);
                a.Emit(census, false, SpeechIntent.Queue, VerbosityLevel.Terse, "FlexBase"); // goes first
                _clock.Advance(454);
                a.Emit(home, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "Home");   // 504 later: re-armed
                _clock.Advance(464);
                a.Emit(home, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "Home");   // 464 later: re-armed
                _clock.Advance(Settle);                                                       // released at 2,568
            });

            Assert.Contains(captured, l =>
                l.Contains($"SpeechArbiter: holding 2 salvage(s) for {Settle} ms behind '{home}' so its own follow-ups go first: 'Slice A, 14.100 USB, yours', 'Changes to the radio"));
            Assert.Contains(captured, l =>
                l.Contains("SpeechArbiter: still holding") && l.Contains("interrupt 2 inside the window")
                && l.Contains("504 ms since the hold began"));
            Assert.Contains(captured, l =>
                l.Contains("SpeechArbiter: still holding") && l.Contains("interrupt 3 inside the window")
                && l.Contains("968 ms since the hold began"));

            // The census was rescued by the second Tab and joins the train
            // behind what was already held; the release names the whole hold.
            Assert.Contains(captured, l =>
                l.Contains($"SpeechArbiter: released 3 of 3 held salvage(s) {968 + Settle} ms after '{home}' (3 interrupt(s) in the window); 1 went first: '{census}'"));
        }

        [Fact]
        public void TheTraceNamesEveryExitFromTheHold()
        {
            string xit = SpeechSubject.ValueField("XIT");
            var captured = CaptureTrace(() =>
            {
                // Refused at the release, with the superseder named.
                var a = NewArbiter();
                a.Emit("XIT +100", false, SpeechIntent.Queue, VerbosityLevel.Terse, "FreqOut", subject: xit);
                _clock.Advance(100);
                a.Emit("RIT off", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "KeyCommands");
                _clock.Advance(1);
                a.Emit("XIT +0", false, SpeechIntent.Queue, VerbosityLevel.Terse, "FreqOut", subject: xit);
                _clock.Advance(Settle);

                // Let go, with the reason.
                var b = NewArbiter();
                b.Emit("Session closed", false, SpeechIntent.Queue, VerbosityLevel.Terse, "t");
                _clock.Advance(100);
                b.Emit("Band changed", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "t");
                _clock.Advance(100);
                b.OnSilenced();
                _clock.Advance(Settle);
            });

            Assert.Contains(captured, l =>
                l.Contains("SpeechArbiter: dropped a salvage (superseded 101 ms after it by 'XIT +0' from FreqOut)")
                && l.Contains("'XIT +100' [subject 'value-field:XIT']"));
            Assert.Contains(captured, l =>
                l.Contains("SpeechArbiter: released 0 of 1 held salvage(s)") && l.Contains("1 went first: 'XIT +0'"));
            Assert.Contains(captured, l =>
                l.Contains("SpeechArbiter: let go of 1 held salvage(s) unspoken, the operator silenced speech: 'Session closed'"));
        }

        /// <summary>
        /// Run <paramref name="body"/> with JJTrace switched on at Verbose and a
        /// listener catching every line — the pattern ReceiveQueueRatchetTests
        /// established. Other tests trace concurrently, so callers assert with
        /// Contains, never on the list's shape.
        /// </summary>
        private static List<string> CaptureTrace(Action body)
        {
            var captured = new List<string>();
            var listener = new CapturingListener(captured);
            bool wasOn = Tracing.On;
            TraceSwitch savedSwitch = Tracing.TheSwitch;
            Trace.Listeners.Add(listener);
            try
            {
                Tracing.TheSwitch = new TraceSwitch("salvage", "salvage") { Level = TraceLevel.Verbose };
                Tracing.On = true;
                body();
            }
            finally
            {
                Tracing.On = wasOn;
                Tracing.TheSwitch = savedSwitch;
                Trace.Listeners.Remove(listener);
            }
            return captured;
        }

        private sealed class CapturingListener : TraceListener
        {
            private readonly List<string> _lines;
            public CapturingListener(List<string> lines) { _lines = lines; }
            public override void Write(string? message) { }
            public override void WriteLine(string? message) { if (message != null) _lines.Add(message); }
        }
    }
}
