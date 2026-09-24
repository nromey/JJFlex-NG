#nullable enable
using System.Collections.Generic;
using System.Linq;
using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    // ────────────────────────────────────────────────────────────────
    //  The recovery rule (#606): recover an unfinished information
    //  obligation when it is still valid, belongs in the receiving context,
    //  and a KNOWN RECOVERY OPPORTUNITY permits it; never use an unrelated
    //  key acknowledgement as permission to replay an old string.
    //
    //  These are deterministic, on a fake clock, through the arbiter's real
    //  routing: no windows, no reader, no radio. The sink hands out tickets
    //  the way the paced delivery does and the tests play the reader's
    //  answers back, because the whole rule turns on what the transport
    //  actually established rather than on what a timer believes.
    //
    //  The anchor is Noel's NVDA transcript of 2026-09-23 05:42. He did not
    //  press Ctrl at any point, so nothing was silenced by him. The
    //  listening sentence was submitted three times and cut at four, four
    //  and two words of ten; the second and third were released 599 and
    //  600 ms after the app acknowledged the JJ key and then Escape. Behind
    //  each attempt sat five connect facts withdrawn before they started,
    //  one of them a clause saying the station restore was unconfirmed —
    //  never spoken once in the whole run.
    //
    //  THE ACCEPTANCE CONDITION IS NOT "IT STOPS REPEATING." It is that the
    //  clause he has never heard reaches him whole.
    //
    //  **And that is not what the test below proves, which is why it was
    //  renamed on 2026-09-23.** It was called
    //  TheClauseHeHasNeverHeard_IsSpokenWhole, and a name that states a
    //  contract is the hardest kind of claim to disagree with — but it drives
    //  no pump, no reader and no window transition, and it supplies the
    //  completion itself. What it really establishes is that the ARBITER
    //  hands the clause over whole, once, spending no rescue, on a sink this
    //  test answers for. That is worth having and it is not the acceptance
    //  condition. The name says so now.
    //
    //  The test that earns the old name lives in SafetySpeechBoundaryTests:
    //  TheClauseHeHasNeverHeard_ReachesTheBackendWhole_ThroughTheRealPump
    //  drives the real paced delivery, so the clause reaches the backend and
    //  the completion comes back from the transport rather than from the
    //  test. What no unit test can reach is the last two steps — NVDA
    //  actually voicing it, and Noel hearing it — and those stay in the
    //  report's press.
    // ────────────────────────────────────────────────────────────────
    public class SpeechRecoveryRuleTests
    {
        private sealed record SinkCall(string Message, bool Interrupt, bool Salvaged, long Ticket);

        private readonly FakeSpeechClock _clock = new();
        private readonly List<SinkCall> _calls = new();
        private readonly List<(long Ticket, string Reason)> _withdrawn = new();
        private readonly HashSet<long> _unsent = new();
        private long _nextTicket;

        private const int Settle = SpeechArbiter.SalvageSettleMs;

        // The sentences from the run, so a reader of this file can match it
        // against the transcript. Their WORDING is not under test and is not
        // this track's to change.
        private const string Listening = "Listening on 21.195 megahertz, USB, 15 meter band, slice A";
        private const string Unconfirmed =
            "The station restore could not be confirmed. Nothing was layered on top of what is there.";
        private const string Slices = "No slices were added by this connect.";
        private const string JjAck = "JJ";
        private const string EscapeAck = "Select Radio";

        private SpeechArbiter NewArbiter() => new SpeechArbiter(
            _clock,
            () => VerbosityLevel.Chatty,
            (message, interrupt, intent, level, origin, salvaged) =>
            {
                long ticket = ++_nextTicket;
                _calls.Add(new SinkCall(message, interrupt, salvaged, ticket));
                _unsent.Add(ticket);
                return SpeechHandoff.TrackedAs(ticket);
            },
            () => { },
            (message, level, intent, origin) => { },
            rate: null,
            isSpeaking: null,
            withdrawUnsent: (ticket, reason) =>
            {
                if (!_unsent.Remove(ticket)) return false;
                _withdrawn.Add((ticket, reason));
                return true;
            });

        private IEnumerable<string> HandedOver() => _calls.Select(c => c.Message);
        private IEnumerable<string> Salvaged() => _calls.Where(c => c.Salvaged).Select(c => c.Message);
        private long TicketOf(string message) => _calls.Last(c => c.Message == message).Ticket;

        /// <summary>
        /// The pump's own report for an utterance it withdrew unspoken: zero
        /// marks, by nobody we can name. This is exactly what
        /// <c>WithdrawForForeignCancel</c> produces.
        /// </summary>
        private void WithdrawnUnstarted(SpeechArbiter a, string message, int words)
        {
            long t = TicketOf(message);
            _unsent.Remove(t);
            a.OnOutcome(t, message, SpeechOutcome.Cancelled(0, words, byUs: false, 0));
        }

        private void CutPartWayByNobodyWeCanName(SpeechArbiter a, string message, int at, int of, int ms)
        {
            long t = TicketOf(message);
            _unsent.Remove(t);
            a.OnOutcome(t, message, SpeechOutcome.Cancelled(at, of, byUs: false, ms));
        }

        // ── The arbiter's half of the acceptance case ──

        [Fact]
        public void TheClauseHeHasNeverHeard_IsHandedOverWholeByTheArbiter_OnASinkThisTestAnswersFor()
        {
            var a = NewArbiter();

            // Home's focus-return status, then the connect facts behind it.
            a.Emit(Listening, false, SpeechIntent.Queue, VerbosityLevel.Terse, "MainWindow", subject: null);
            a.Emit(Unconfirmed, false, SpeechIntent.Queue, VerbosityLevel.Terse, "connect",
                SpeechSubject.ProfileStationOutcome);
            a.Emit(Slices, false, SpeechIntent.Queue, VerbosityLevel.Terse, "connect",
                SpeechSubject.ProfileStationSlices);

            // The reader starts the listening line and something that is not
            // us cuts it four words into ten. Everything queued behind it is
            // withdrawn by the pump, at zero marks, before it says a word.
            _clock.Advance(1200);
            CutPartWayByNobodyWeCanName(a, Listening, at: 4, of: 10, ms: 1126);
            WithdrawnUnstarted(a, Unconfirmed, words: 16);
            WithdrawnUnstarted(a, Slices, words: 7);

            // The operator presses the JJ key and we acknowledge it. Under the
            // old contract this acknowledgement swept the whole ledger and, 600
            // ms later, started the listening sentence again from its first
            // word while the two connect facts each spent a rescue at
            // admission without being spoken.
            _clock.Advance(3600);
            a.Emit(JjAck, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys", SpeechSubject.JjKeyHelp);
            _clock.Advance(Settle);

            // The sentence he heard three beginnings of is not begun a fourth
            // time: its cause is unknown and a keypress acknowledgement is not
            // permission.
            Assert.DoesNotContain(Listening, Salvaged());

            // And the clause that was never spoken once is handed over WHOLE.
            Assert.Contains(Unconfirmed, Salvaged());
            Assert.Contains(Slices, Salvaged());
            Assert.Equal(Unconfirmed, _calls.First(c => c.Salvaged).Message);

            // It cost no rescue, because nothing had been heard: had the
            // operator pressed Escape too, and had that hand-over also been
            // withdrawn unstarted, it would still be owed.
            WithdrawnUnstarted(a, Unconfirmed, words: 16);
            WithdrawnUnstarted(a, Slices, words: 7);
            _clock.Advance(300);
            a.Emit(EscapeAck, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys", SpeechSubject.WhereYouAre);
            _clock.Advance(Settle);
            Assert.Equal(2, _calls.Count(c => c.Salvaged && c.Message == Unconfirmed));

            // Once the reader says it finished, it is done: heard is heard,
            // and no later interrupt says it again.
            long said = TicketOf(Unconfirmed);
            a.OnOutcome(said, Unconfirmed, SpeechOutcome.Completed(16, 16, 5200));
            _clock.Advance(300);
            a.Emit("Slice A", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys", SpeechSubject.WhereYouAre);
            _clock.Advance(Settle);
            Assert.Equal(2, _calls.Count(c => c.Salvaged && c.Message == Unconfirmed));
        }

        [Fact]
        public void ThreeBeginningsAndNoEnd_CannotHappenAgain()
        {
            // The symptom, stated as its own test so the mechanism is pinned
            // and not merely implied by the acceptance case above: original
            // plus two rescues was exactly the ceiling the repetition reached,
            // and each rescue restarted from the first word.
            var a = NewArbiter();
            a.Emit(Listening, false, SpeechIntent.Queue, VerbosityLevel.Terse, "MainWindow", subject: null);
            _clock.Advance(1200);
            CutPartWayByNobodyWeCanName(a, Listening, at: 4, of: 10, ms: 1126);

            foreach (string ack in new[] { JjAck, EscapeAck, "Slice A", "Slice B" })
            {
                _clock.Advance(600);
                a.Emit(ack, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys", SpeechSubject.WhereYouAre);
                _clock.Advance(Settle);
            }

            Assert.Empty(Salvaged());
            Assert.Equal(1, _calls.Count(c => c.Message == Listening));
        }

        [Fact]
        public void HowFarItGot_SurvivesAHandOver()
        {
            // ReleaseHeld zeroed the marks on every re-hand, discarding the one
            // fact that says whether the operator heard any of it. The entry
            // keeps its high-water mark now, which is what lets the rule tell a
            // repeat from a first hearing across successive attempts.
            var a = NewArbiter();
            a.Emit(Unconfirmed, false, SpeechIntent.Queue, VerbosityLevel.Terse, "connect",
                SpeechSubject.ProfileStationOutcome);

            // Cut by US at word nine: we caused it, so we owe the put-back,
            // and a rescue IS spent because nine words were heard.
            long first = TicketOf(Unconfirmed);
            _unsent.Remove(first);
            a.OnOutcome(first, Unconfirmed, SpeechOutcome.Cancelled(9, 16, byUs: true, 2600));

            a.Emit(JjAck, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys", SpeechSubject.JjKeyHelp);
            _clock.Advance(Settle);
            Assert.Single(Salvaged());

            // Withdrawn unstarted this time — no progress — and then cut by us
            // again. The rescue count keeps climbing because the FIRST attempt
            // was heard in part, so the cap still ends it.
            WithdrawnUnstarted(a, Unconfirmed, words: 16);
            _clock.Advance(300);
            a.Emit(EscapeAck, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys", SpeechSubject.WhereYouAre);
            _clock.Advance(Settle);
            Assert.Equal(2, _calls.Count(c => c.Salvaged));

            WithdrawnUnstarted(a, Unconfirmed, words: 16);
            _clock.Advance(300);
            a.Emit("Slice A", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys", SpeechSubject.WhereYouAre);
            _clock.Advance(Settle);
            Assert.Equal(2, _calls.Count(c => c.Salvaged));
        }

        [Fact]
        public void AnUnheardClause_IsStillRetiredByItsOwner()
        {
            // "Still owed" is not "immortal". The connect's own next verdict on
            // the same subject makes the earlier one worthless, and that is
            // what retires it — not a timer, and not a keypress.
            var a = NewArbiter();
            a.Emit(Unconfirmed, false, SpeechIntent.Queue, VerbosityLevel.Terse, "connect",
                SpeechSubject.ProfileStationOutcome);
            WithdrawnUnstarted(a, Unconfirmed, words: 16);

            a.Emit("Your station is back as you left it.", false, SpeechIntent.Queue, VerbosityLevel.Terse,
                "connect", SpeechSubject.ProfileStationOutcome);

            _clock.Advance(300);
            a.Emit(JjAck, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys", SpeechSubject.JjKeyHelp);
            _clock.Advance(Settle);

            Assert.DoesNotContain(Unconfirmed, Salvaged());
            Assert.Contains("Your station is back as you left it.", Salvaged());
        }

        // ── Supersession reaches the pump ──

        [Fact]
        public void Supersession_TakesUnsentWorkBackOutOfTheDeliveryQueue()
        {
            // Marking an entry superseded stopped it being RESCUED and did
            // nothing about a copy the pump had already been handed and not
            // yet started — so a progress heartbeat could still be spoken
            // immediately behind the line that answered it.
            var a = NewArbiter();
            a.Emit("Still searching for radios.", false, SpeechIntent.Queue, VerbosityLevel.Terse,
                "ProgressVoice", SpeechSubject.Progress);
            long heartbeat = TicketOf("Still searching for radios.");

            a.Emit("Searching for radios.", false, SpeechIntent.Queue, VerbosityLevel.Terse,
                "ProgressVoice", SpeechSubject.Progress);

            Assert.Contains(_withdrawn, w => w.Ticket == heartbeat);
            Assert.DoesNotContain(heartbeat, _unsent);
        }

        [Fact]
        public void AnExplicitSupersede_AlsoTakesItBack()
        {
            // The progress voice's last line is made worthless by the dialog
            // that answers it, and that dialog's title is not a progress line —
            // so the voice says so itself when it stops.
            var a = NewArbiter();
            a.Emit("Still searching for radios.", false, SpeechIntent.Queue, VerbosityLevel.Terse,
                "ProgressVoice", SpeechSubject.Progress);
            long heartbeat = TicketOf("Still searching for radios.");

            a.Supersede(SpeechSubject.Progress, "the radio picker opened", "ProgressVoice");

            Assert.Contains(_withdrawn, w => w.Ticket == heartbeat);
        }

        [Fact]
        public void WorkTheReaderAlreadyHas_IsNotClaimedBack()
        {
            // The honest half: once the reader has it, supersession does what
            // it always did — it stops the rescue and nothing more. The pump
            // says so by refusing the withdrawal, and nothing here pretends
            // otherwise.
            var a = NewArbiter();
            a.Emit("Still searching for radios.", false, SpeechIntent.Queue, VerbosityLevel.Terse,
                "ProgressVoice", SpeechSubject.Progress);
            long heartbeat = TicketOf("Still searching for radios.");
            _unsent.Remove(heartbeat);              // the reader started it

            a.Emit("Searching for radios.", false, SpeechIntent.Queue, VerbosityLevel.Terse,
                "ProgressVoice", SpeechSubject.Progress);

            Assert.DoesNotContain(_withdrawn, w => w.Ticket == heartbeat);

            // And it is not rescued either, which was always the contract.
            CutPartWayByNobodyWeCanName(a, "Still searching for radios.", at: 1, of: 4, ms: 300);
            _clock.Advance(300);
            a.Emit(JjAck, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys", SpeechSubject.JjKeyHelp);
            _clock.Advance(Settle);
            Assert.DoesNotContain("Still searching for radios.", Salvaged());
        }

        // ── The safety path ──

        [Fact]
        public void ASafetyWarning_IsLedgered_SoAnInterruptionDelaysItRatherThanLosingIt()
        {
            // #571's third HIGH finding, and the one that matters most: the
            // Urgent branch put the warning in no ledger at all, so the one
            // sentence that must survive an interruption was the one sentence
            // that could not.
            const string cut = "Transmit stopped. 80 percent of your power is coming back on ANT2.";
            var a = NewArbiter();
            a.Urgent(cut, VerbosityLevel.Critical, "TransmitKillSwitch", SpeechSubject.ReflectedPowerCut);

            CutPartWayByNobodyWeCanName(a, cut, at: 3, of: 11, ms: 900);

            // Ordinary navigation cannot take its slot: the next interrupt
            // puts it back, unlike an ordinary sentence cut by the same
            // unknown cause.
            _clock.Advance(1000);
            a.Emit("Slice A", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys", SpeechSubject.WhereYouAre);
            _clock.Advance(Settle);
            Assert.Contains(cut, Salvaged());
        }

        [Fact]
        public void ASafetyObligation_IsNotRetiredByTheCapOrTheCeiling_ButItsAutomaticAttemptsAreBounded()
        {
            // ── NARROWED 2026-09-23, Sprint 45 Track K. ──
            //
            // This asserted FOUR hand-overs and that is what it is no longer
            // allowed to say. The cap and the ceiling still may not retire a
            // safety obligation — that half is unchanged and is asserted
            // below — but "exempt from the cap and the ceiling" had become
            // "exempt from everything", and Astra's contract is explicit that
            // repeated partial progress must not buy infinite retries:
            //
            //   "One initial automatic attempt and at most one automatic
            //    retry for an unchanged episode's material fact."
            //
            // So the attempts are bounded by ProtectedAutomaticAttempts and
            // the obligation is PAUSED rather than dropped. "We have stopped
            // trying for now" and "nothing is owed" are different states and
            // only the first one is true here.
            const string cut = "Transmit stopped. 80 percent of your power is coming back on ANT2.";
            var a = NewArbiter();
            a.Urgent(cut, VerbosityLevel.Critical, "TransmitKillSwitch", SpeechSubject.ReflectedPowerCut);

            // Four rounds, each cut part way — past MaxSalvages, and past the
            // fifteen-second lifetime, with PROGRESS every time so neither the
            // rescue cap, the ceiling nor the no-progress back-off can be what
            // stops it.
            for (int i = 0; i < 4; i++)
            {
                CutPartWayByNobodyWeCanName(a, cut, at: 3, of: 11, ms: 900);
                _clock.Advance(6000);
                a.Emit("Slice A", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys", SpeechSubject.WhereYouAre);
                _clock.Advance(Settle);
            }

            // The initial hand-over was not a salvage, so the budget of two
            // automatic attempts buys exactly one re-hand.
            Assert.Equal(SpeechArbiter.ProtectedAutomaticAttempts - 1,
                _calls.Count(c => c.Salvaged && c.Message == cut));

            // And it is still OWED, which is the half that must not be read
            // off an absence: paused and retained sound exactly like dropped.
            var owed = a.OwedSafetyObligations;
            Assert.Contains(owed, o => o.Message == cut);
            Assert.True(owed.Single(o => o.Message == cut).AutomaticSpeechPaused);
            Assert.False(owed.Single(o => o.Message == cut).SilencedByOperator);
            Assert.Equal(SpeechSubject.ReflectedPowerCut, owed.Single(o => o.Message == cut).Subject);
        }

        [Fact]
        public void ASafetyObligation_SurvivesAnUrgentDiscard_AndASilence_ButSilenceIsNotUndoneByThatNextKey()
        {
            // ── INVERTED IN ITS SECOND HALF 2026-09-23, Sprint 45 Track K. ──
            //
            // The first half stands and is unchanged: clearing the runnable
            // text for a cut must not erase the cut, nor another safety
            // outcome still owed, and a Silence is not the operator saying
            // they understood the condition.
            //
            // The second half asserted that the very next unrelated interrupt
            // rescues BOTH warnings, 600 ms after the operator pressed the
            // shut-up key. Sol named that as a defect and the register's #182
            // ruling is why: Ctrl means silence, and an unrelated keypress is
            // not permission to undo it. Keeping the fact and immediately
            // re-speaking it is the same defiance in a better-named place.
            //
            // So: still owed, still reachable, NOT automatically re-offered.
            const string first = "Transmit stopped. 80 percent of your power is coming back on ANT2.";
            const string second = "Your microphone is not reaching the radio.";
            var a = NewArbiter();
            a.Emit("PC audio on.", false, SpeechIntent.Queue, VerbosityLevel.Terse, "audio", SpeechSubject.PcAudio);
            a.Urgent(first, VerbosityLevel.Critical, "TransmitKillSwitch", SpeechSubject.ReflectedPowerCut);
            CutPartWayByNobodyWeCanName(a, first, at: 3, of: 11, ms: 900);

            // A second, unrelated urgent warning. Its discard takes the
            // ordinary backlog and leaves the first warning owed.
            a.Urgent(second, VerbosityLevel.Critical, "PttSafetyController", SpeechSubject.NoTransmitAudio);
            CutPartWayByNobodyWeCanName(a, second, at: 1, of: 7, ms: 200);

            // THE CONTROL, before the silence: both are recoverable, which is
            // what makes the assertion after it mean something. Without this
            // the test could pass because the warnings were never retained at
            // all — an absence proving the wrong thing.
            _clock.Advance(1000);
            a.Emit("Slice A", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys", SpeechSubject.WhereYouAre);
            _clock.Advance(Settle);
            Assert.Contains(first, Salvaged());
            Assert.Contains(second, Salvaged());
            Assert.DoesNotContain("PC audio on.", Salvaged());

            CutPartWayByNobodyWeCanName(a, first, at: 3, of: 11, ms: 900);
            CutPartWayByNobodyWeCanName(a, second, at: 1, of: 7, ms: 200);
            int before = _calls.Count(c => c.Salvaged);

            a.OnSilenced();

            _clock.Advance(1000);
            a.Emit("Slice B", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys", SpeechSubject.WhereYouAre);
            _clock.Advance(Settle);

            // Nothing was handed over again by that key.
            Assert.Equal(before, _calls.Count(c => c.Salvaged));

            // But both facts are still OWED — not acknowledged, not disabled,
            // not erased. This is the assertion that must NOT be read off an
            // absence: "retained but paused" and "silently dropped" are the
            // same silence, so the obligations are looked at rather than
            // inferred from what was not spoken.
            var owed = a.OwedSafetyObligations;
            Assert.Contains(owed, o => o.Message == first);
            Assert.Contains(owed, o => o.Message == second);
            Assert.All(owed, o => Assert.True(o.SilencedByOperator,
                "the operator's Ctrl is recorded against the obligation, not acted on by deleting it"));

            // And still not acknowledged: silencing says nothing about whether
            // the operator understood the condition.
            Assert.Equal(SpeechSubject.ReflectedPowerCut, owed.Single(o => o.Message == first).Subject);
            Assert.Equal(SpeechSubject.NoTransmitAudio, owed.Single(o => o.Message == second).Subject);
        }

        [Fact]
        public void ANewerSafetyEpisode_RetiresAnUnheardOlderOne()
        {
            // The one lifecycle event that may retire a safety obligation is
            // its owner's own newer statement on the same subject — which is
            // the subject that used to be dropped on the way through the
            // urgent route.
            const string first = "Transmit stopped. 80 percent of your power is coming back on ANT2.";
            const string second = "Transmit stopped. 60 percent of your power is coming back on ANT2.";
            var a = NewArbiter();
            a.Urgent(first, VerbosityLevel.Critical, "TransmitKillSwitch", SpeechSubject.ReflectedPowerCut);
            CutPartWayByNobodyWeCanName(a, first, at: 3, of: 11, ms: 900);

            a.Urgent(second, VerbosityLevel.Critical, "TransmitKillSwitch", SpeechSubject.ReflectedPowerCut);
            CutPartWayByNobodyWeCanName(a, second, at: 2, of: 11, ms: 700);

            _clock.Advance(1000);
            a.Emit("Slice A", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys", SpeechSubject.WhereYouAre);
            _clock.Advance(Settle);

            Assert.DoesNotContain(first, Salvaged());
            Assert.Contains(second, Salvaged());
        }

        [Fact]
        public void ASafetyObligation_ThatSaysNothingRepeatedly_BacksOff_ButIsStillOwed()
        {
            // "Never give up" and "chase every keypress forever" are different
            // promises. Repeated hand-overs that say nothing at all pause the
            // automatic attempts; the obligation stays in the ledger.
            //
            // ── ITS LAST CLAUSE INVERTED 2026-09-23, Sprint 45 Track K. ──
            //
            // It used to end: "a delivery completing anywhere is the meaningful
            // new opportunity that lets it try again." That is the same shape
            // as the defect this whole rule exists to end — an unrelated
            // success treated as permission, one step along from an unrelated
            // keypress treated as permission. Astra's contract says it in as
            // many words: *"Never retry merely because a key acknowledgement
            // or unrelated speech completed."*
            //
            // A recovery event has to be about the CHANNEL, not about the
            // machine having managed to say something else. So the opportunity
            // is now a backend-recovery EDGE — the transition out of a channel
            // that was refusing or absent — and this test asserts both halves:
            // an ordinary completion on a healthy channel grants nothing, and
            // a real edge grants exactly one probe.
            const string cut = "Transmit stopped. 80 percent of your power is coming back on ANT2.";
            var a = NewArbiter();
            a.Urgent(cut, VerbosityLevel.Critical, "TransmitKillSwitch", SpeechSubject.ReflectedPowerCut);

            for (int i = 0; i < SpeechArbiter.ProtectedAutomaticAttempts; i++)
            {
                WithdrawnUnstarted(a, cut, words: 11);
                _clock.Advance(300);
                a.Emit("Slice A", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys", SpeechSubject.WhereYouAre);
                _clock.Advance(Settle);
            }
            int attempts = _calls.Count(c => c.Salvaged && c.Message == cut);
            Assert.True(attempts > 0, "the control: it really was handed over before it backed off");

            // Backed off: the next interrupt does not hand it over again.
            WithdrawnUnstarted(a, cut, words: 11);
            _clock.Advance(300);
            a.Emit("Slice B", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys", SpeechSubject.WhereYouAre);
            _clock.Advance(Settle);
            Assert.Equal(attempts, _calls.Count(c => c.Salvaged && c.Message == cut));

            // AND AN ORDINARY COMPLETION CHANGES NOTHING. The channel was
            // never in trouble, so there is no edge to take, and a sentence
            // about a slice finishing is not evidence about this warning.
            a.OnOutcome(TicketOf("Slice B"), "Slice B", SpeechOutcome.Completed(2, 2, 400));
            _clock.Advance(300);
            a.Emit("Slice C", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys", SpeechSubject.WhereYouAre);
            _clock.Advance(Settle);
            Assert.Equal(attempts, _calls.Count(c => c.Salvaged && c.Message == cut));
        }

        [Fact]
        public void ABackendRecoveryEdge_GrantsTheBackedOffSafetyObligation_ExactlyOneMoreAttempt()
        {
            // The positive control for the test above, and the other half of
            // the rule: a completion is only a recovery event when the channel
            // it completed on had actually been failing. One probe per edge,
            // and the probe does not clear what the episode has already spent,
            // so a flapping reader cannot manufacture fresh budgets.
            const string cut = "Transmit stopped. 80 percent of your power is coming back on ANT2.";
            var a = NewArbiter();
            a.Urgent(cut, VerbosityLevel.Critical, "TransmitKillSwitch", SpeechSubject.ReflectedPowerCut);

            for (int i = 0; i < SpeechArbiter.ProtectedAutomaticAttempts; i++)
            {
                WithdrawnUnstarted(a, cut, words: 11);
                _clock.Advance(300);
                a.Emit("Slice A", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys", SpeechSubject.WhereYouAre);
                _clock.Advance(Settle);
            }
            int attempts = _calls.Count(c => c.Salvaged && c.Message == cut);

            // The reader refuses outright — NVDA asleep for this application.
            // THAT is what makes the channel known bad.
            a.OnOutcome(TicketOf("Slice A"), "Slice A",
                SpeechOutcome.Unknown(SpeechUnknownReason.Refused, "asleep", 5));

            // And then something really is heard again: the edge.
            _clock.Advance(300);
            a.Emit("Slice B", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys", SpeechSubject.WhereYouAre);
            a.OnOutcome(TicketOf("Slice B"), "Slice B", SpeechOutcome.Completed(2, 2, 400));
            _clock.Advance(300);
            a.Emit("Slice C", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys", SpeechSubject.WhereYouAre);
            _clock.Advance(Settle);
            Assert.Equal(attempts + 1, _calls.Count(c => c.Salvaged && c.Message == cut));

            // Exactly one. A second completion on the same healthy channel is
            // not a second edge.
            WithdrawnUnstarted(a, cut, words: 11);
            _clock.Advance(300);
            a.Emit("Slice D", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys", SpeechSubject.WhereYouAre);
            a.OnOutcome(TicketOf("Slice D"), "Slice D", SpeechOutcome.Completed(2, 2, 400));
            _clock.Advance(300);
            a.Emit("Slice E", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys", SpeechSubject.WhereYouAre);
            _clock.Advance(Settle);
            Assert.Equal(attempts + 1, _calls.Count(c => c.Salvaged && c.Message == cut));
        }

        [Fact]
        public void ASafetyObligation_GoesToTheReaderAheadOfOrdinaryWork()
        {
            const string cut = "Transmit stopped. 80 percent of your power is coming back on ANT2.";
            var a = NewArbiter();
            a.Emit("PC audio on.", false, SpeechIntent.Queue, VerbosityLevel.Terse, "audio", SpeechSubject.PcAudio);
            WithdrawnUnstarted(a, "PC audio on.", words: 3);

            a.Urgent(cut, VerbosityLevel.Critical, "TransmitKillSwitch", SpeechSubject.ReflectedPowerCut);
            CutPartWayByNobodyWeCanName(a, cut, at: 3, of: 11, ms: 900);

            _clock.Advance(1000);
            a.Emit("Slice A", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys", SpeechSubject.WhereYouAre);
            _clock.Advance(Settle);

            Assert.Equal(cut, Salvaged().First());
        }

        [Fact]
        public void AnOrdinaryWarningIsStillNotResurrectedByASilence()
        {
            // The control for the Silence case: an ordinary utterance the
            // operator silenced stays silenced. Only a protected obligation is
            // kept, and it is kept because silence is not acknowledgement that
            // the operator understood a safety condition.
            var a = NewArbiter();
            a.Emit("PC audio on.", false, SpeechIntent.Queue, VerbosityLevel.Terse, "audio", SpeechSubject.PcAudio);
            a.OnSilenced();
            _clock.Advance(300);
            a.Emit("Slice A", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys", SpeechSubject.WhereYouAre);
            _clock.Advance(Settle);
            Assert.Empty(Salvaged());
        }

        // ── A guard on the shape of the whole thing ──

        [Fact]
        public void NothingIsEverRecoveredWithoutSomethingSayingItWasLost()
        {
            // The negative half of the rule, which is the whole fix: an
            // elapsed estimate, a missing completion event or an unrelated
            // keypress is not proof of loss. Here the reader says it FINISHED,
            // and no amount of later interrupting brings it back.
            var a = NewArbiter();
            a.Emit(Unconfirmed, false, SpeechIntent.Queue, VerbosityLevel.Terse, "connect",
                SpeechSubject.ProfileStationOutcome);
            a.OnOutcome(TicketOf(Unconfirmed), Unconfirmed, SpeechOutcome.Completed(16, 16, 5200));

            foreach (string ack in new[] { JjAck, EscapeAck, "Slice A" })
            {
                _clock.Advance(300);
                a.Emit(ack, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys", SpeechSubject.WhereYouAre);
                _clock.Advance(Settle);
            }

            Assert.Empty(Salvaged());
            Assert.Single(HandedOver().Where(m => m == Unconfirmed));
        }
    }
}
