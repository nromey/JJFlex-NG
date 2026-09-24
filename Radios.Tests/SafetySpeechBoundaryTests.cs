#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Radios.Alarms;
using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    // ────────────────────────────────────────────────────────────────────
    //  THE COMPOSITION EVIDENCE FOR #611 — Tracks I and J on one tree.
    //
    //  Astra's warning about the acceptance tests is the reason this file
    //  is shaped the way it is:
    //
    //      "A mock sink call list alone proves none of those properties."
    //
    //  The defect these tests exist for is a RACE BETWEEN SCHEDULERS, and a
    //  list of sink calls cannot see it: every call in that list happened,
    //  in the order the arbiter decided, and the arbiter's decision is
    //  exactly what is in question. The two existing suites each miss it
    //  from a different side — Track I's priority tests use an untracked
    //  sink and advance an estimated duration, so no tracked cut is ever
    //  under an old alarm's late completion; Track J's ordering test asserts
    //  the order of Salvaged() calls, which is the arbiter's own output and
    //  not the order the reader was given anything.
    //
    //  So these drive the REAL PACED DELIVERY, with a completion channel the
    //  test controls, and assert on _ch.Seen — the order the backend was
    //  actually handed utterances — plus the obligations the arbiter still
    //  holds. Both public adapters are reproduced exactly as
    //  ScreenReaderOutput spells them (SafetyRouteShapeTests below pins that
    //  claim against the source, so it cannot rot quietly), and the alarm
    //  cue stage runs as itself.
    //
    //  What these tests are NOT: proof that a real mismatch triggers a cut,
    //  that RF ceased, or that NVDA said anything. See the report's press.
    // ────────────────────────────────────────────────────────────────────
    public sealed class SafetySpeechBoundaryTests : IDisposable
    {
        private const string Cut =
            "Transmit stopped. Eighty percent of your power is coming back on ANT2. You are no longer on the air.";
        private const string AlarmA = "PA temperature 61 degrees C. Release transmit now.";
        private const string AlarmB = "Supply voltage, before the fuse, is 11.90 volts.";

        private static string SubjectA => SpeechSubject.OperatorAlarm("pa");
        private static string SubjectB => SpeechSubject.OperatorAlarm("volts");

        private readonly FakeCompletionChannel _ch = new();
        private readonly PacedSpeechDelivery _pump;
        private readonly SpeechArbiter _arbiter;

        /// <summary>Tickets whose outcome the test holds back, to model an answer delayed on its way to the arbiter.</summary>
        private readonly HashSet<long> _heldTickets = new();
        private readonly List<(long Ticket, string Text, SpeechOutcome Outcome)> _heldOutcomes = new();
        private readonly object _gate = new();

        public SafetySpeechBoundaryTests()
        {
            SpeechArbiter? arbiter = null;

            // The pump, real, with short deadlines so a test never waits on
            // the production ones. Its outcome route is the only place these
            // tests interpose: an outcome for a held ticket waits until the
            // test releases it, which is precisely the "delayed on the way to
            // the arbiter" of Astra's interleaving. Nothing else is faked.
            _pump = new PacedSpeechDelivery(
                (text, interrupt) => SpeechDelivery.Accepted,
                (ticket, text, outcome) =>
                {
                    lock (_gate)
                    {
                        if (_heldTickets.Contains(ticket))
                        {
                            _heldOutcomes.Add((ticket, text, outcome));
                            return;
                        }
                    }
                    arbiter!.OnOutcome(ticket, text, outcome);
                },
                _ => 30000, cancelGraceMs: 500, escapeGraceMs: 500, markGraceMs: 500);

            // The sink, spelled as ScreenReaderOutput.EmitCore spells it when
            // the backend offers a completion channel.
            _arbiter = new SpeechArbiter(
                new SystemSpeechClock(),
                () => VerbosityLevel.Chatty,
                (message, interrupt, intent, level, origin, salvaged) =>
                {
                    long t = interrupt ? _pump.Interrupt(_ch, message) : _pump.Enqueue(_ch, message);
                    return SpeechHandoff.TrackedAs(t);
                },
                silenceBackend: () => { },
                recordGated: (message, level, intent, origin) => { });
            arbiter = _arbiter;
        }

        public void Dispose()
        {
            _ch.ReleaseAll();
            _pump.Dispose();
        }

        // ── The two public adapters, spelled as ScreenReaderOutput spells them ──

        /// <summary>ScreenReaderOutput.Speak(..., SpeechIntent.Urgent, ...) — the transmit-safety route.</summary>
        private void SpeakSafety(string message, string subject) =>
            _arbiter.Urgent(message, VerbosityLevel.Critical, "PttSafetyController.cs:1178 OnReflected", subject);

        /// <summary>ScreenReaderOutput.SpeakAlarm(message, subject, refresh) — the operator-alarm route.</summary>
        private void SpeakAlarm(string message, string subject, Func<string?> refresh) =>
            _arbiter.UrgentAlarm(message, VerbosityLevel.Critical, "AlarmDelivery.cs Warn", subject, refresh);

        // ── Observation helpers. Every one reads the BACKEND, not the sink. ──

        private void Hold(long ticket) { lock (_gate) _heldTickets.Add(ticket); }

        private void ReleaseHeldOutcome(long ticket)
        {
            (long Ticket, string Text, SpeechOutcome Outcome) held;
            lock (_gate)
            {
                _heldTickets.Remove(ticket);
                held = _heldOutcomes.Single(o => o.Ticket == ticket);
                _heldOutcomes.Remove(held);
            }
            _arbiter.OnOutcome(held.Ticket, held.Text, held.Outcome);
        }

        /// <summary>Replace a held Cancelled answer with a Completed one, so both late shapes can be driven.</summary>
        private void ReleaseHeldOutcomeAsCompleted(long ticket)
        {
            (long Ticket, string Text, SpeechOutcome Outcome) held;
            lock (_gate)
            {
                _heldTickets.Remove(ticket);
                held = _heldOutcomes.Single(o => o.Ticket == ticket);
                _heldOutcomes.Remove(held);
            }
            int words = NvdaCompletionChannel.SplitWords(held.Text).Length;
            _arbiter.OnOutcome(held.Ticket, held.Text, SpeechOutcome.Completed(words, words, 900));
        }

        /// <summary>Everything the BACKEND was handed, in the order it was handed it.</summary>
        private IReadOnlyList<string> BackendStarts() => _ch.Seen.Select(s => s.Text).ToList();

        private static void SettleForBackendWork() => Thread.Sleep(350);

        private IReadOnlyList<SpeechArbiter.OwedSafety> Owed() => _arbiter.OwedSafetyObligations;

        // ────────────────────────────────────────────────────────────────
        //  The interleaving itself, in both late shapes.
        // ────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(true)]   // A's late answer is a Completed
        [InlineData(false)]  // A's late answer is a Cancelled
        public void AnOldAlarmsLateAnswer_DoesNotReleaseTheCutsTurn_AndTheCutRecordSurvives(bool lateCompleted)
        {
            // Astra's permitted ordering, needing no radio fault:
            //
            //   Alarm A has a tracked attempt. Its backend completion is
            //   delayed on the way to the arbiter. A transmit cut takes over
            //   and is retained as protected. Alarm B waits. A's late answer
            //   arrives; under the branches as they stood it matched the
            //   remembered last-alarm ticket, set the shared deadline to now,
            //   rearmed the alarm timer, and at the following release B
            //   passed refresh, invoked the FULL discard and interrupted the
            //   sink. The cut was erased from the retained state and its own
            //   later cancellation found no record.
            SpeakAlarm(AlarmA, SubjectA, () => AlarmA);
            var callA = _ch.WaitForCall();
            Assert.Equal(AlarmA, callA.Text);
            Hold(callA.Ticket);

            // The cut. It pre-empts A, so the pump cancels A in flight — and
            // that answer is the one held back.
            SpeakSafety(Cut, SpeechSubject.ReflectedPowerCut);
            var callCut = _ch.WaitForCall();
            Assert.Equal(Cut, callCut.Text);

            // B arrives while the cut holds the turn, and must wait.
            SpeakAlarm(AlarmB, SubjectB, () => AlarmB);
            SettleForBackendWork();
            Assert.Equal(new[] { AlarmA, Cut }, BackendStarts());
            Assert.Equal(1, _arbiter.AlarmPendingCount);

            // A's delayed answer lands, in whichever shape.
            if (lateCompleted) ReleaseHeldOutcomeAsCompleted(callA.Ticket);
            else ReleaseHeldOutcome(callA.Ticket);

            // Long enough for any alarm timer an old answer might have armed.
            Thread.Sleep(SpeechArbiter.AlarmRetrySettleMs + 400);

            // NO B BACKEND START INSIDE THE PROTECTED TURN.
            Assert.Equal(new[] { AlarmA, Cut }, BackendStarts());

            // AND THE CUT RECORD STILL EXISTS, looked at rather than inferred
            // from the silence — "retained" and "erased" both sound like
            // nothing from outside.
            Assert.Contains(Owed(), o => o.Message == Cut && o.Subject == SpeechSubject.ReflectedPowerCut);

            // A VALID CURRENT-CUT COMPLETION RELEASES B. This is the positive
            // control: without it the two assertions above are satisfied by a
            // channel that simply stopped working.
            _ch.Complete(callCut, 2400);
            var callB = _ch.WaitForCall();
            Assert.Equal(AlarmB, callB.Text);
            Assert.Equal(new[] { AlarmA, Cut, AlarmB }, BackendStarts());

            // And the cut left the obligations on its own real answer, which
            // is the only thing that may discharge it.
            Assert.DoesNotContain(Owed(), o => o.Message == Cut);
        }

        [Fact]
        public void ACutLongerThanItsEstimate_KeepsTheTurn_AndIsNotInterruptedByAWaitingAlarm()
        {
            // The other half of the same defect, and it needs no late
            // callback at all: an ordinary expiry of an estimated duration
            // could interrupt a tracked cut that was still progressing. An
            // estimate is never permission to interrupt an attempt the reader
            // is demonstrably still working on.
            // A short sentence on purpose, so its estimate can be outwaited
            // in wall time. The property is about the mechanism, not the
            // wording: a long cut has a longer estimate and the same rule.
            const string shortCut = "Transmit stopped.";
            int estimate = SpeechArbiter.EstimateSpokenMs(shortCut);
            Assert.True(estimate <= SpeechArbiter.SalvageMinMs + 400,
                $"the fixture needs a short estimate to outwait; it was {estimate} ms");

            SpeakSafety(shortCut, SpeechSubject.ReflectedPowerCut);
            var callCut = _ch.WaitForCall();

            SpeakAlarm(AlarmA, SubjectA, () => AlarmA);
            Assert.Equal(1, _arbiter.AlarmPendingCount);

            // Well past what this sentence is estimated to take, with the
            // reader still holding it and still reporting progress.
            for (int i = 0; i < 4; i++) { _ch.Mark(callCut); Thread.Sleep(200); }
            Thread.Sleep(estimate + 1200);

            Assert.Equal(new[] { shortCut }, BackendStarts());
            Assert.Contains(Owed(), o => o.Message == shortCut);

            // The control again: the cut's OWN answer is what lets the alarm
            // through, so the silence above was the rule and not a fault.
            _ch.Complete(callCut, estimate + 3000);
            var callA = _ch.WaitForCall();
            Assert.Equal(AlarmA, callA.Text);
        }

        [Fact]
        public void AnAlarmArrivingAfterAPartlyDeliveredCut_WaitsAndTheCutStaysOwed()
        {
            // The cut is cut part-way by something outside the application.
            // The operator heard the beginning and not the end, so the fact is
            // owed — and an alarm arriving next is not the event that may
            // decide otherwise.
            SpeakSafety(Cut, SpeechSubject.ReflectedPowerCut);
            var callCut = _ch.WaitForCall();
            _ch.Mark(callCut); _ch.Mark(callCut); _ch.Mark(callCut);
            _ch.CancelFromOutside(callCut, marksReached: 3, elapsedMs: 900);
            SettleForBackendWork();

            SpeakAlarm(AlarmA, SubjectA, () => AlarmA);
            var callA = _ch.WaitForCall();
            Assert.Equal(AlarmA, callA.Text);

            // The alarm goes ahead — the cut's turn ended on the cut's own
            // answer — and the CUT IS STILL OWED, part-delivered rather than
            // delivered.
            var owed = Owed().Single(o => o.Message == Cut);
            Assert.Equal(SpeechSubject.ReflectedPowerCut, owed.Subject);
            Assert.False(owed.SilencedByOperator);
        }

        [Fact]
        public void AnAlarmHandoffThatNeverReachesABackend_LeavesTheTurnFree_AndSaysSo()
        {
            // "Not reached" is suppression, an absent backend or a refusal.
            // The turn must not be left occupied by an attempt that never
            // started — that would silently block every later safety
            // announcement — and the alarm's own record stays with the alarm
            // subsystem, which is why nothing is retained here.
            var refusing = new SpeechArbiter(
                new SystemSpeechClock(),
                () => VerbosityLevel.Chatty,
                (message, interrupt, intent, level, origin, salvaged) => SpeechHandoff.NotReached,
                silenceBackend: () => { },
                recordGated: (message, level, intent, origin) => { });

            refusing.UrgentAlarm(AlarmA, VerbosityLevel.Critical, "AlarmDelivery.cs Warn", SubjectA, () => AlarmA);
            Assert.Equal(0, refusing.AlarmPendingCount);

            // The turn is free: a transmit-safety warning admitted straight
            // after is retained rather than queued behind a phantom.
            refusing.Urgent(Cut, VerbosityLevel.Critical, "PttSafetyController", SpeechSubject.ReflectedPowerCut);
            var owed = refusing.OwedSafetyObligations;
            Assert.Contains(owed, o => o.Message == Cut);

            // And the safety fact was retained even though the reader took
            // nothing — which is the whole of "retention before the first
            // handoff". Before this, a refused warning left no record at all.
            Assert.Single(owed);
        }

        // ────────────────────────────────────────────────────────────────
        //  Silence, including across the cue seam.
        // ────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(true)]    // the operator presses Ctrl while the tone plays
        [InlineData(false)]   // the positive control: nobody does
        public void SilenceDuringTheWarningTone_StandsTheSentenceDown_WithoutTouchingTheAlarm(bool silence)
        {
            // The 750 ms tone lead means that for the whole of it the warning
            // exists nowhere the arbiter can see: it has not been submitted.
            // A quiet policy that lives only in the ledger therefore cannot
            // revoke this continuation, and the operator who presses Ctrl
            // during the tone hears the sentence start anyway. The cohort is
            // captured when the tone is armed and compared when it fires.
            //
            // The real AlarmDelivery, from a real meter reading through the
            // real service — the seam under test is its own, so stubbing it
            // would be the mock-call-list failure one layer along.
            using var cue = new RealCueStage(() => _arbiter.SafetyQuietGeneration);
            cue.DeliverReading(63.5f);

            Assert.Equal(new[] { "tone" }, cue.Sounds);
            Assert.Empty(cue.Spoken);                           // still behind the tone
            cue.Clock.Advance(AlarmDelivery.ToneLeadMs - 1);
            Assert.Empty(cue.Spoken);

            if (silence) _arbiter.OnSilenced();                 // Ctrl, during the tone

            cue.Clock.Advance(2);

            if (silence)
            {
                Assert.Empty(cue.Spoken);
                // Nothing about the alarm itself moved: it is still active,
                // still in the list, and nothing was acknowledged.
                Assert.NotNull(cue.Snapshot());
                Assert.Equal(AlarmConditionState.Active, cue.Snapshot()!.Condition);
                Assert.Equal(AlarmNotificationState.Unacknowledged, cue.Snapshot()!.Notification);
            }
            else
            {
                // THE POSITIVE CONTROL, without which the emptiness above
                // proves nothing at all: the identical sequence with no Ctrl
                // does reach the speaker.
                Assert.Single(cue.Spoken);
                Assert.Contains("63", cue.Spoken[0]);
            }
        }

        [Fact]
        public void ASilencedCut_StaysOwed_AndIsNotHandedBackByTheNextKey()
        {
            SpeakSafety(Cut, SpeechSubject.ReflectedPowerCut);
            var callCut = _ch.WaitForCall();
            _ch.Mark(callCut);
            _ch.CancelFromOutside(callCut, marksReached: 1, elapsedMs: 300);
            SettleForBackendWork();

            _arbiter.OnSilenced();

            // An ordinary interrupt, then its settle window: under the branch
            // as it stood this handed the warning straight back, 600 ms after
            // the operator asked for quiet.
            _arbiter.Emit("Slice A", interrupt: true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys",
                SpeechSubject.WhereYouAre);
            Thread.Sleep(SpeechArbiter.SalvageSettleMs + 400);

            Assert.DoesNotContain(Cut, BackendStarts().Skip(1));

            // Owed, recorded as silenced, and not acknowledged.
            var owed = Owed().Single(o => o.Message == Cut);
            Assert.True(owed.SilencedByOperator);
            Assert.Equal(SpeechSubject.ReflectedPowerCut, owed.Subject);
        }

        // ────────────────────────────────────────────────────────────────
        //  The test that earns the name SpeechRecoveryRuleTests' acceptance
        //  case used to carry.
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void TheClauseHeHasNeverHeard_ReachesTheBackendWhole_ThroughTheRealPump()
        {
            // The 05:42 run, driven through the real paced delivery rather
            // than a sink the test answers for. The clause saying the station
            // restore was unconfirmed was withdrawn before it ever started on
            // every attempt, spent both its rescues at admission, and was
            // never spoken once. Noel has never heard it.
            //
            // What is different here from the arbiter-only version: the pump
            // really holds the queue, the withdrawal is the pump's own
            // WithdrawForForeignCancel rather than three hand-written
            // outcomes, the hand-over goes to the backend, and the completion
            // comes back FROM THE TRANSPORT. What is still missing, and is
            // the whole of what a bench sitting is for: NVDA actually voicing
            // it, and the operator hearing it.
            const string Listening = "Listening on 21.195 megahertz, USB, 15 meter band, slice A";
            const string Unconfirmed =
                "The station restore could not be confirmed. Nothing was layered on top of what is there.";
            const string Slices = "No slices were added by this connect.";

            _arbiter.Emit(Listening, interrupt: false, SpeechIntent.Queue, VerbosityLevel.Terse, "MainWindow", null);
            _arbiter.Emit(Unconfirmed, interrupt: false, SpeechIntent.Queue, VerbosityLevel.Terse, "connect",
                SpeechSubject.ProfileStationOutcome);
            _arbiter.Emit(Slices, interrupt: false, SpeechIntent.Queue, VerbosityLevel.Terse, "connect",
                SpeechSubject.ProfileStationSlices);

            // Only the first is with the reader; the other two wait in OUR
            // queue, which is the whole point of #521 and the reason they can
            // be taken back at all.
            var callListening = _ch.WaitForCall();
            Assert.Equal(Listening, callListening.Text);
            SettleForBackendWork();
            Assert.Equal(new[] { Listening }, BackendStarts());

            // Something that is not us cuts it four words into ten. The pump
            // withdraws everything behind it, unspoken, and reports each at
            // zero marks — which is the fact the whole rule turns on.
            _ch.Mark(callListening); _ch.Mark(callListening);
            _ch.Mark(callListening); _ch.Mark(callListening);
            _ch.CancelFromOutside(callListening, marksReached: 4, elapsedMs: 1126);
            SettleForBackendWork();
            Assert.Equal(new[] { Listening }, BackendStarts());   // nothing was started behind it

            // The operator presses the JJ key and we acknowledge it.
            _arbiter.Emit("JJ", interrupt: true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys",
                SpeechSubject.JjKeyHelp);
            var callAck = _ch.WaitForCall();
            Assert.Equal("JJ", callAck.Text);
            _ch.Complete(callAck, 200);

            // The settle window closes and the train goes to the reader.
            Thread.Sleep(SpeechArbiter.SalvageSettleMs + 500);
            var clause = _ch.WaitForCall();
            Assert.Equal(Unconfirmed, clause.Text);

            // WHOLE, and the sentence he heard three beginnings of is not
            // begun a fourth time.
            Assert.Equal(Unconfirmed, BackendStarts().Last());
            Assert.Single(BackendStarts().Where(t => t == Listening));

            // And the completion comes from the transport, not from the test
            // declaring it: the reader says it finished, and the obligation
            // goes. This is the difference the old name claimed and did not
            // have.
            _ch.Complete(clause, 5200);
            var next = _ch.WaitForCall();
            Assert.Equal(Slices, next.Text);
            _ch.Complete(next, 1800);

            _arbiter.Emit("Slice A", interrupt: true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys",
                SpeechSubject.WhereYouAre);
            Thread.Sleep(SpeechArbiter.SalvageSettleMs + 500);
            Assert.Single(BackendStarts().Where(t => t == Unconfirmed));
        }

        /// <summary>
        /// The REAL cue stage: a meter reading into a real
        /// <see cref="AlarmService"/>, the real <see cref="AlarmDelivery"/>
        /// with its tone lead and its timer continuation, and a speaker that
        /// records what actually arrived. The same wiring
        /// <c>AlarmDeliveryTests</c> uses, with the quiet cohort added.
        /// </summary>
        private sealed class RealCueStage : IDisposable
        {
            private const string Serial = "1234-5678-9012-3456";
            private static readonly MeterDescriptor Pa = Alarms.PaTemperatureReplayFixture.Meter;

            private readonly string _root = Path.Combine(Path.GetTempPath(), "jjflex-k-cue-" + Guid.NewGuid().ToString("N"));
            private readonly Alarms.FakeAlarmFeed _feed = new();
            private readonly ManualAlarmClock _alarmClock = new() { NowMs = 10_000 };
            private readonly AlarmService _service;
            private readonly AlarmDelivery _delivery;
            private readonly Speaker _speaker = new();

            public FakeSpeechClock Clock { get; } = new();
            public List<string> Sounds { get; } = new();
            public IReadOnlyList<string> Spoken => _speaker.Spoken;

            public RealCueStage(Func<long> quietGeneration)
            {
                _service = new AlarmService(_feed, new AlarmDefinitionStore(_root), _alarmClock, null, startWatchdog: false);
                _delivery = new AlarmDelivery(_service, _speaker, () => Sounds.Add("tone"), Clock,
                    warningsSoundEnabled: () => true, speechSuppressed: () => false, speechAvailable: () => true,
                    quietGeneration: quietGeneration);
                _feed.Connect(Serial, Pa);
                Assert.True(_service.Add(
                    AlarmPresets.Build(AlarmPresets.PaTemperature, Pa, Serial, "pa") with { Enabled = true }));
            }

            public void DeliverReading(float value)
            {
                _alarmClock.Advance(2000);
                _feed.Deliver(Pa, value);
                Assert.True(_service.DrainDispatch(2000));
            }

            public AlarmSnapshot? Snapshot() => _service.SnapshotOf("pa");

            public void Dispose()
            {
                _delivery.Dispose();
                _service.Dispose();
                try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }

            private sealed class Speaker : IAlarmSpeaker
            {
                public readonly List<string> Spoken = new();
                public void SpeakWarning(string text, string subject, Func<string?> refresh) { lock (Spoken) Spoken.Add(text); }
                public void SpeakStatus(string text, VerbosityLevel level, string subject) { }
            }
        }
    }

    /// <summary>
    /// The claim <see cref="SafetySpeechBoundaryTests"/> rests on: that the
    /// calls it makes are the calls the PUBLIC adapters make.
    ///
    /// <para>The acceptance tests drive <c>SpeechArbiter</c> directly, because
    /// the static <c>ScreenReaderOutput</c> holds a process-wide arbiter on
    /// the system clock with no backend in a test run — so driving the static
    /// would exercise the not-reached path and prove nothing about ordering.
    /// The cost of that choice is that the adapters could be re-plumbed
    /// underneath the tests without a single one going red. This reads the
    /// source and refuses that.</para>
    /// </summary>
    public class SafetyRouteShapeTests
    {
        private static string ScreenReaderOutputSource() =>
            File.ReadAllText(Path.Combine(RepoRoot(), "Radios", "ScreenReaderOutput.cs"));

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        [Fact]
        public void ThePublicUrgentRoute_StillCarriesTheSubjectIntoTheArbitersSafetyPath()
        {
            Assert.Contains("_arbiter.Urgent(message, level, origin, subject);", ScreenReaderOutputSource());
        }

        [Fact]
        public void ThePublicAlarmRoute_StillGoesToTheArbitersAlarmPath_WithItsSubjectAndRefresh()
        {
            string src = ScreenReaderOutputSource();
            Assert.Contains("public static void SpeakAlarm(", src);
            Assert.Contains("_arbiter.UrgentAlarm(message, VerbosityLevel.Critical,", src);
            Assert.Contains("subject, refresh);", src);
        }

        [Fact]
        public void TheAlarmCueStage_StillReadsTheLiveQuietCohort()
        {
            // Without this line the cue stage compares 0 against 0 for ever
            // and a Ctrl during the warning tone does nothing — which would
            // look exactly like the tests passing.
            string src = File.ReadAllText(Path.Combine(RepoRoot(), "Radios", "Alarms", "AlarmDelivery.cs"));
            Assert.Contains("() => ScreenReaderOutput.SafetyQuietGeneration", src);
        }
    }
}
