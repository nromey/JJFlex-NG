#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    // ────────────────────────────────────────────────────────────────
    //  A protected safety sentence that claims a PRESENT state carries
    //  its producer's answer to "is that still the state", and is asked
    //  before every automatic replay (Astra's Track IJK review, blocker 6;
    //  #617: each fact carries the condition that makes it true).
    //
    //  The case Astra traced: the reflected-power cut says "you are no
    //  longer on the air"; an unheard copy is retained; the operator keys
    //  again; an unrelated interrupt rescues the copy and it is replayed
    //  DURING the new transmission. The finite retry count did not make the
    //  claim true. Noel's 2026-09-30 ruling ships the alarms on their own
    //  route and defers the fact store; it does not waive a false
    //  operator-facing claim, and this is the half of the remedy that fits
    //  on the own route: a producer validity contract, with the trace as the
    //  record. The reachable history surface stays the fact store's.
    // ────────────────────────────────────────────────────────────────
    public class SafetyValidityTests
    {
        private sealed record SinkCall(string Message, bool Salvaged, long Ticket);

        private readonly FakeSpeechClock _clock = new();
        private readonly List<SinkCall> _calls = new();
        private long _nextTicket;

        private const int Settle = SpeechArbiter.SalvageSettleMs;
        private const string Cut = "Transmit stopped. 80 percent of your power is coming back on ANT2. You are no longer on the air.";

        private SpeechArbiter NewArbiter() => new SpeechArbiter(
            _clock,
            () => VerbosityLevel.Chatty,
            (message, interrupt, intent, level, origin, salvaged) =>
            {
                long t = ++_nextTicket;
                _calls.Add(new SinkCall(message, salvaged, t));
                return SpeechHandoff.TrackedAs(t);
            },
            () => { },
            (message, level, intent, origin) => { });

        private IEnumerable<string> Salvaged() => _calls.Where(c => c.Salvaged).Select(c => c.Message);
        private long TicketOf(string m) => _calls.Last(c => c.Message == m).Ticket;

        private void CutPartWayByNobodyWeCanName(SpeechArbiter a, string message, int at, int of, int ms)
            => a.OnOutcome(TicketOf(message), message, SpeechOutcome.Cancelled(at, of, byUs: false, ms));

        [Theory]
        [InlineData(true)]    // the operator keyed again before the replay: withdrawn
        [InlineData(false)]   // the positive control: same transmission episode, replayed as before
        public void AnUnheardCut_IsWithdrawnRatherThanReplayed_OnceTheOperatorHasKeyedAgain(bool keyedAgain)
        {
            // The producer's episode counter, as PttSafetyController keeps it:
            // advanced at every transmit start, captured when the cut speaks.
            int transmitEpisode = 1;
            int cutEpisode = transmitEpisode;
            var a = NewArbiter();
            a.Urgent(Cut, VerbosityLevel.Critical, "PttSafetyController", SpeechSubject.ReflectedPowerCut,
                stillValid: () => transmitEpisode == cutEpisode);
            CutPartWayByNobodyWeCanName(a, Cut, at: 3, of: 17, ms: 900);
            Assert.Contains(a.OwedSafetyObligations, o => o.Message == Cut);

            if (keyedAgain) transmitEpisode = 2;   // PttDown: a new transmission

            // The ordinary interrupt that used to rescue the copy, and its settle.
            _clock.Advance(1000);
            a.Emit("Slice A", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys", SpeechSubject.WhereYouAre);
            _clock.Advance(Settle);

            if (keyedAgain)
            {
                Assert.DoesNotContain(Cut, Salvaged());
                // Withdrawn, not merely paused: the owed list no longer claims
                // it, because nothing true is owed any more. The trace is the
                // record of what was not delivered.
                Assert.DoesNotContain(a.OwedSafetyObligations, o => o.Message == Cut);
            }
            else
            {
                Assert.Contains(Cut, Salvaged());
                Assert.Contains(a.OwedSafetyObligations, o => o.Message == Cut);
            }
        }

        [Fact]
        public void TheQuestionIsAskedAtEveryReplay_NotOnlyTheFirst()
        {
            // Still true at the first replay; keyed again before the second.
            int transmitEpisode = 1;
            int cutEpisode = transmitEpisode;
            var a = NewArbiter();
            a.Urgent(Cut, VerbosityLevel.Critical, "PttSafetyController", SpeechSubject.ReflectedPowerCut,
                stillValid: () => transmitEpisode == cutEpisode);
            CutPartWayByNobodyWeCanName(a, Cut, at: 3, of: 17, ms: 900);

            _clock.Advance(1000);
            a.Emit("Slice A", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys", SpeechSubject.WhereYouAre);
            _clock.Advance(Settle);
            Assert.Equal(1, Salvaged().Count(m => m == Cut));
            CutPartWayByNobodyWeCanName(a, Cut, at: 2, of: 17, ms: 600);

            transmitEpisode = 2;
            _clock.Advance(1000);
            a.Emit("Slice B", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys", SpeechSubject.WhereYouAre);
            _clock.Advance(Settle);
            Assert.Equal(1, Salvaged().Count(m => m == Cut));   // no second replay
            Assert.DoesNotContain(a.OwedSafetyObligations, o => o.Message == Cut);
        }

        [Fact]
        public void AProducerThatCannotAnswer_DoesNotGetItsPresentStateClaimReplayed()
        {
            // "Cannot say" is not permission to tell the operator a present
            // state. The sentence is not replayed, and the trace says why.
            var a = NewArbiter();
            a.Urgent(Cut, VerbosityLevel.Critical, "PttSafetyController", SpeechSubject.ReflectedPowerCut,
                stillValid: () => throw new InvalidOperationException("the controller is gone"));
            CutPartWayByNobodyWeCanName(a, Cut, at: 3, of: 17, ms: 900);
            _clock.Advance(1000);
            a.Emit("Slice A", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys", SpeechSubject.WhereYouAre);
            _clock.Advance(Settle);
            Assert.DoesNotContain(Cut, Salvaged());
        }

        [Fact]
        public void ASentenceWithNoValidityAnswer_IsReplayedUnderTheExistingRules()
        {
            // The contract is opt-in: a producer that declares nothing keeps
            // the behaviour every existing test pins.
            var a = NewArbiter();
            a.Urgent(Cut, VerbosityLevel.Critical, "PttSafetyController", SpeechSubject.ReflectedPowerCut);
            CutPartWayByNobodyWeCanName(a, Cut, at: 3, of: 17, ms: 900);
            _clock.Advance(1000);
            a.Emit("Slice A", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys", SpeechSubject.WhereYouAre);
            _clock.Advance(Settle);
            Assert.Contains(Cut, Salvaged());
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  The producer's episode, driven for real (Astra's Track IJK2 review,
    //  blocker 1 — introduced by the B6 fix above).
    //
    //  The first fix counted transmissions with an integer advanced beside
    //  every SetTx(true) in PttSafetyController. One of those sites is a held
    //  PTT becoming a transmit lock, where RF never stops — so a reflected-
    //  power warning cut short during the hold, still true during the lock,
    //  was withdrawn as history while power was still coming back. The tests
    //  above used an integer stand-in and could not see it, and the source
    //  pin REQUIRED the increment before every SetTx(true), so it pinned the
    //  defect.
    //
    //  The rule now lives in Radios.Speech.TransmitEpisode, fed by the
    //  controller's one RF site, and these tests drive that real class through
    //  the real arbiter with the exact command sequence the controller issues.
    //  What they cannot do is run PttSafetyController itself: it is WPF, this
    //  project does not reference it, and JJFlexWpf.Tests is run only under
    //  the window-free filter. The sequence the controller issues is pinned by
    //  source in TransmitStateClaimValidityTests below.
    // ────────────────────────────────────────────────────────────────
    public class TransmitEpisodeTests
    {
        private sealed record SinkCall(string Message, bool Salvaged, long Ticket);

        private readonly FakeSpeechClock _clock = new();
        private readonly List<SinkCall> _calls = new();
        private long _nextTicket;

        private const int Settle = SpeechArbiter.SalvageSettleMs;
        private const string Warning = "40 percent of your power is coming back on ANT2. Transmit will be cut if it stays there.";

        private SpeechArbiter NewArbiter() => new SpeechArbiter(
            _clock,
            () => VerbosityLevel.Chatty,
            (message, interrupt, intent, level, origin, salvaged) =>
            {
                long t = ++_nextTicket;
                _calls.Add(new SinkCall(message, salvaged, t));
                return SpeechHandoff.TrackedAs(t);
            },
            () => { },
            (message, level, intent, origin) => { });

        private IEnumerable<string> Salvaged() => _calls.Where(c => c.Salvaged).Select(c => c.Message);
        private long TicketOf(string m) => _calls.Last(c => c.Message == m).Ticket;

        private void CutPartWayByNobodyWeCanName(SpeechArbiter a, string message, int at, int of, int ms)
            => a.OnOutcome(TicketOf(message), message, SpeechOutcome.Cancelled(at, of, byUs: false, ms));

        /// <summary>
        /// Astra's exact case, and its invalidating controls. The producer's
        /// sequence for each case is the one PttSafetyController issues:
        /// PttDown is RF on; ToggleLock from a hold is RF on AGAIN with no off
        /// between; PttUp or any GoIdle is RF off.
        /// </summary>
        [Theory]
        [InlineData("hold-to-lock")]     // RF kept on: the warning is still about this RF, and is replayed
        [InlineData("stop")]             // RF off: the transmission ended, the warning is withdrawn
        [InlineData("stop-then-start")]  // a real re-key: a new transmission, the warning is withdrawn
        public void AReflectedWarningCutDuringAHeldPtt_SurvivesTheHoldBecomingALock_AndNotARealStopOrStart(string then)
        {
            var episode = new TransmitEpisode();
            episode.RfIs(true);                                          // PttDown: SetTx(true)
            var a = NewArbiter();
            a.Urgent(Warning, VerbosityLevel.Critical, "PttSafetyController", SpeechSubject.ReflectedPowerWarning,
                stillValid: episode.StillThisTransmission());            // CheckReflectedPower, during the hold
            CutPartWayByNobodyWeCanName(a, Warning, at: 3, of: 15, ms: 900);
            Assert.Contains(a.OwedSafetyObligations, o => o.Message == Warning);

            switch (then)
            {
                case "hold-to-lock":
                    episode.RfIs(true);                                  // ToggleLock from PttHold: EnterLocked, SetTx(true), no SetTx(false)
                    break;
                case "stop":
                    episode.RfIs(false);                                 // PttUp: GoIdle, SetTx(false)
                    break;
                case "stop-then-start":
                    episode.RfIs(false);                                 // GoIdle
                    episode.RfIs(true);                                  // PttDown again
                    break;
            }

            // The lock announcement, or any other ordinary interrupt, and its settle.
            _clock.Advance(1000);
            a.Emit("Transmitting, locked", true, SpeechIntent.Interrupt, VerbosityLevel.Critical, "PttSafetyController", null);
            _clock.Advance(Settle);

            if (then == "hold-to-lock")
            {
                Assert.Contains(Warning, Salvaged());
                Assert.Contains(a.OwedSafetyObligations, o => o.Message == Warning);
            }
            else
            {
                Assert.DoesNotContain(Warning, Salvaged());
                Assert.DoesNotContain(a.OwedSafetyObligations, o => o.Message == Warning);
            }
        }

        // ── created-state tests: what the RF rule now produces ──

        [Fact]
        public void TheEpisodeAdvancesOnTheOffToOnEdgeOnly()
        {
            var e = new TransmitEpisode();
            Assert.Equal(0, e.Current);
            Assert.False(e.RfOn);
            e.RfIs(true);  Assert.Equal(1, e.Current); Assert.True(e.RfOn);    // PttDown
            e.RfIs(true);  Assert.Equal(1, e.Current); Assert.True(e.RfOn);    // hold becomes lock: same RF
            e.RfIs(false); Assert.Equal(1, e.Current); Assert.False(e.RfOn);   // unkey
            e.RfIs(false); Assert.Equal(1, e.Current); Assert.False(e.RfOn);   // Dispose after an unkey: nothing starts
            e.RfIs(true);  Assert.Equal(2, e.Current); Assert.True(e.RfOn);    // a real re-key
        }

        /// <summary>
        /// The controller feeds the episode "own key OR external watch". An
        /// external transmit (a transmit-check probe) that begins while the
        /// operator's own key is down, and outlasts it, is one continuous RF —
        /// one episode — and a warning spoken under it stays valid until the
        /// LAST of the two goes off. This is the state the follow-up Astra
        /// named: Idle is no longer read as "no possible transmission".
        /// </summary>
        [Fact]
        public void RfThatPassesFromTheOwnersKeyToAnExternalWatch_IsOneTransmission()
        {
            var e = new TransmitEpisode();
            e.RfIs(true);                              // own key down
            Func<bool> warned = e.StillThisTransmission();
            e.RfIs(true);                              // BeginExternalTransmitWatch: own || external, still on
            e.RfIs(true);                              // own key up, external still up: still on
            Assert.True(warned());
            Assert.Equal(1, e.Current);
            e.RfIs(false);                             // EndExternalTransmitWatch: RF off at last
            Assert.False(warned());
            Assert.Equal(1, e.Current);
        }

        [Fact]
        public void AnExternalTransmitAlone_IsATransmission_AndItsWarningIsValidWhileItRuns()
        {
            var e = new TransmitEpisode();
            e.RfIs(true);                              // BeginExternalTransmitWatch, operator Idle
            Func<bool> warned = e.StillThisTransmission();
            Assert.True(warned());
            e.RfIs(false);                             // EndExternalTransmitWatch
            Assert.False(warned());
            e.RfIs(true);                              // the next probe
            Assert.False(warned());                    // and the old warning does not come back for it
        }

        [Fact]
        public void TheEndClaim_IsTrueUntilTheNextTransmission_WhetherOrNotRfIsOnWhenAsked()
        {
            var e = new TransmitEpisode();
            e.RfIs(true);
            e.RfIs(false);                             // GoIdle: SetTx(false) first ...
            Func<bool> ended = e.NoTransmissionSince(); // ... then the cut sentence captures the ended episode
            Assert.True(ended());
            e.RfIs(false);                             // Dispose, a second off: still true
            Assert.True(ended());
            e.RfIs(true);                              // keyed again
            Assert.False(ended());
        }

        [Fact]
        public void TheRunningClaim_IsFalseTheMomentRfStops_EvenBeforeAnyNewTransmission()
        {
            var e = new TransmitEpisode();
            e.RfIs(true);
            Func<bool> running = e.StillThisTransmission();
            Assert.True(running());
            e.RfIs(false);
            Assert.False(running());
        }
    }

    /// <summary>
    /// The producer's sequence, pinned by source because
    /// <c>PttSafetyController</c> is WPF and unreachable from here without a
    /// window: RF is commanded in ONE place and the episode is told there and
    /// at the external watch; nothing advances it by hand; the hold-to-lock
    /// path keeps RF on; and every safety outcome and warning that claims a
    /// present transmit state passes the episode's own answer.
    /// </summary>
    public class TransmitStateClaimValidityTests
    {
        private static string Ptt() =>
            File.ReadAllText(Path.Combine(RepoRoot(), "JJFlexWpf", "PttSafetyController.cs"));

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        /// <summary>The text of one method, from its signature to its matching close brace.</summary>
        private static string Body(string src, string signature)
        {
            int at = src.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, "no method with signature: " + signature);
            int open = src.IndexOf('{', at);
            int depth = 0;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}' && --depth == 0) return src.Substring(at, i - at + 1);
            }
            throw new InvalidOperationException("unbalanced braces after " + signature);
        }

        [Fact]
        public void TheEpisodeIsToldByTheRfCommandAndTheExternalWatch_AndAdvancedByNothingElse()
        {
            string src = Ptt();
            // The integer counter and its hand-advances are gone.
            Assert.DoesNotContain("_transmitEpisode++", src);
            Assert.Contains("private readonly Radios.Speech.TransmitEpisode _transmitEpisode", src);
            // ONE place tells the episode about RF.
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(src, @"\.RfIs\(").Count);
            Assert.Contains("_transmitEpisode.RfIs(_rfCommandedOn || _externalWatchers > 0)", Body(src, "private void NoteRf()"));
            // Fed from the one RF command site and from both ends of the external watch.
            string setTx = Body(src, "private void SetTx(bool on)");
            Assert.Contains("_rfCommandedOn = on;", setTx);
            Assert.Contains("NoteRf();", setTx);
            Assert.Contains("NoteRf();", Body(src, "public void BeginExternalTransmitWatch()"));
            Assert.Contains("NoteRf();", Body(src, "public void EndExternalTransmitWatch()"));
            // Positive control: both transmit starts still command RF through SetTx.
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(src, @"SetTx\(true\);").Count);
        }

        [Fact]
        public void TheHoldToLockPath_KeepsRfOn_SoItIsTheSameEpisode()
        {
            string src = Ptt();
            // ToggleLock still takes a held PTT into EnterLocked.
            Assert.Contains("State == PttState.PttHold", Body(src, "public void ToggleLock()"));
            // EnterLocked commands RF on and never off: with the episode fed
            // by the RF edge, that is what makes hold-to-lock one transmission.
            string enterLocked = Body(src, "private void EnterLocked()");
            Assert.Contains("SetTx(true);", enterLocked);
            Assert.DoesNotContain("SetTx(false)", enterLocked);
            // Positive control: the method that ends a transmission does say off.
            Assert.Contains("SetTx(false);", Body(src, "private void GoIdle(string speechMessage"));
        }

        [Fact]
        public void TheSafetyOutcome_AndTheTransmitWarnings_CarryTheEpisodesOwnAnswer()
        {
            string src = Ptt();
            // GoIdle's Urgent outcome: valid while no new transmission has begun.
            Assert.Contains("stillValid: forceSpeech ? _transmitEpisode.NoTransmissionSince() : null", src);
            // The reflected-power warning and the about-to-end warning: valid
            // while THIS transmission's RF is still on.
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(src,
                @"stillValid: _transmitEpisode\.StillThisTransmission\(\)").Count);
            // And no answer reads the state label as the RF: Idle under an
            // external watch IS a transmission.
            Assert.DoesNotContain("stillValid: () => State != PttState.Idle", src);
        }
    }
}
