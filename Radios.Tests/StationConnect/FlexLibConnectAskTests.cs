using System;
using System.Linq;
using System.Reflection;
using Radios;
using Radios.StationConnect;
using Xunit;

namespace Radios.Tests.StationConnect
{
    /// <summary>
    /// FlexLib's OWN connect-time <c>profile global info</c> (#639, repair
    /// round two, 2026-10-20). <c>Radio.Connect</c> calls
    /// <c>GetProfileLists()</c>, which sends that ask itself, so the evidence
    /// log never saw it: its answer was classed as the radio reporting on its
    /// own account, and if it arrived after the first slice a global load
    /// restored, the completion policy could confirm a load that was still
    /// rebuilding the station (Codex Sol, round-one re-verify; it is a
    /// reachable source-level gap, never observed at the radio).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Noel ruled at 08:15: treat FlexLib's connect-time request as
    /// outstanding until its answer arrives, so a list that might be its
    /// answer never confirms; and if it has not answered by the time the load
    /// is sent, the restore stays unconfirmed. At 08:13: on every radio, no
    /// model gate.
    /// </para>
    /// <para>
    /// RED on <c>3c5493fc</c>, run 2026-10-20 before the repair, with
    /// <see cref="StationHarness.FlexLibConnectTimeAskIsOutstanding"/> a no-op
    /// (the base has nothing that records the ask, which is the defect): the
    /// late-answer test and the unanswered-ask test both came back
    /// RestoredConfirmed; the before-the-send control passed. The
    /// production-seam test in <see cref="FlexLibConnectAskProductionSeamTests"/>
    /// failed on the base because the late list was classed unsolicited.
    /// </para>
    /// </remarks>
    public class FlexLibConnectAskTests
    {
        [Fact]
        public void FlexLibsConnectTimeAnswer_ArrivingAfterTheFirstRestoredSlice_DoesNotConfirm()
        {
            // FlexLib asked at connect and the radio has not answered by the
            // time the load goes out. The answer then arrives between the
            // first and second restored slices, the rest of the load follows,
            // and the radio's own re-report comes after the last slice. Under
            // the ruling nothing after this send can be told from FlexLib's
            // answer, so the load never confirms: it ends Unconfirmed at the
            // phase bound, keeping the three slices that came.
            var h = new StationHarness(StationPolicies.Defaults());
            h.ArrangeOwnerReconnect();
            h.FlexLibConnectTimeAskIsOutstanding();
            h.Port.OnGlobalLoadSent = _ => h.OwnSliceArrives();                              // the first restored slice
            bool wholeLoadDelivered = false;
            h.Waiter.Then(() => h.RadioReportsGlobalList(h.Port.InventoryNames.ToArray()))  // FlexLib's late answer
                    .Then(() =>
                    {
                        h.OwnSliceArrives();
                        h.OwnSliceArrives();
                        h.RadioReportsGlobalList(h.Port.InventoryNames.ToArray());          // the radio's own re-report
                        wholeLoadDelivered = true;
                    });
            long before = h.Clock.NowMs;

            var r = h.Run();

            Assert.Equal(StationOutcome.Unconfirmed, r.Outcome);
            Assert.True(wholeLoadDelivered, "the coordinator ended before the rest of the load was delivered");
            Assert.False(r.StationEstablished);
            Assert.True(r.LoadOutstanding);
            Assert.Equal(3, r.OwnSlicesAtEnd);
            Assert.True(h.Clock.NowMs - before >= h.Deadlines.StationPhaseMs, "ended before the phase bound");
            Assert.Contains("FlexLib's connect-time profile list request", r.Reason);
            Assert.Contains(h.Port.Trace, l => l.Contains("FlexLib's connect-time profile list request") && l.Contains("unanswered when the load was sent"));
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
        }

        [Fact]
        public void FlexLibsConnectTimeAnswer_ArrivingBeforeTheSend_IsConsumed_AndTheRadiosOwnReReportStillConfirms()
        {
            // The ordinary connect, as every archived trace shows it: FlexLib
            // asks, the radio answers within about 100 ms of the connect, and
            // the load goes out long after. The answer is consumed as
            // FlexLib's; the re-report after the last restored slice is the
            // radio's own and confirms, exactly as before this round.
            var h = new StationHarness(StationPolicies.Defaults());
            h.FlexLibConnectTimeAskIsOutstanding();
            h.ArrangeOwnerReconnect();                         // its connect-time list is FlexLib's answer
            h.Port.OnGlobalLoadSent = _ => h.RadioCompletesTheLoadAsMeasured(slices: 3);

            var r = h.Run();

            Assert.Equal(StationOutcome.RestoredConfirmed, r.Outcome);
            Assert.True(r.StationEstablished);
            Assert.Equal(3, r.OwnSlicesAtEnd);
            Assert.DoesNotContain(h.Port.Trace, l => l.Contains("unanswered when the load was sent"));
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
        }

        [Fact]
        public void AnUnansweredFlexLibAsk_HoldsTheAttemptUnconfirmed_AtThePhaseBound()
        {
            // FlexLib asked and the radio never answers it. The load's own
            // re-report is the only list after the send, and it might be
            // FlexLib's answer: fail-closed, not a guess.
            var h = new StationHarness(StationPolicies.Defaults());
            h.ArrangeOwnerReconnect();
            h.FlexLibConnectTimeAskIsOutstanding();
            h.Port.OnGlobalLoadSent = _ => h.RadioCompletesTheLoadAsMeasured(slices: 1);
            long before = h.Clock.NowMs;

            var r = h.Run();

            Assert.Equal(StationOutcome.Unconfirmed, r.Outcome);
            Assert.False(r.StationEstablished);
            Assert.True(r.LoadOutstanding);
            Assert.Equal(1, r.OwnSlicesAtEnd);
            Assert.True(h.Clock.NowMs - before >= h.Deadlines.StationPhaseMs, "ended before the phase bound");
            Assert.Contains("FlexLib's connect-time profile list request", r.Reason);
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
        }

        [Fact]
        public void OurAskMadeWhileFlexLibsWasOpen_LeavesItAmbiguousWhichWasAnswered_SoTheLoadDoesNotConfirm()
        {
            // FlexLib asked; before its answer came, our fresh read asked too;
            // ONE list arrived before the send. Nothing in a list says which
            // ask it answers, so FlexLib's answer may be the one still owed.
            // By the ruling, the load does not confirm.
            var h = new StationHarness(StationPolicies.Defaults());
            h.OurClientAdded();
            h.Port.Selection = h.Port.Facts.WantedGlobal;
            h.FlexLibConnectTimeAskIsOutstanding();
            h.OurInventoryAskIsStillOutstanding();
            h.RadioReportsGlobalList(h.Port.InventoryNames.ToArray());     // one answer, of two asks
            h.Port.OnGlobalLoadSent = _ => h.RadioCompletesTheLoadAsMeasured(slices: 1);

            var r = h.Run();

            Assert.Equal(StationOutcome.Unconfirmed, r.Outcome);
            Assert.Contains("FlexLib's connect-time profile list request", r.Reason);
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
        }

        [Fact]
        public void OurAskMadeAfterFlexLibsWasAnswered_KeepsRoundOnesRule_TheLateAnswerIsConsumedAndTheReReportConfirms()
        {
            // The control for the case above: FlexLib's answer is unambiguous
            // (it arrived before our ask went out), so an ask of ours still
            // open at the send is round one's case, unchanged.
            var h = new StationHarness(StationPolicies.Defaults());
            h.FlexLibConnectTimeAskIsOutstanding();
            h.ArrangeOwnerReconnect();                                     // FlexLib's answer
            h.OurInventoryAskIsStillOutstanding();
            h.Waiter.Then(() => h.RadioReportsGlobalList(h.Port.InventoryNames.ToArray()))   // our late answer, consumed
                    .Then(() => h.RadioCompletesTheLoadAsMeasured(slices: 2));

            var r = h.Run();

            Assert.Equal(StationOutcome.RestoredConfirmed, r.Outcome);
            Assert.Equal(2, r.OwnSlicesAtEnd);
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
        }

        // ── the log's accounting, directly ─────────────────────────────────

        private const int Gen = 11;
        private static ProfileEvidenceLog NewLog()
        {
            var log = new ProfileEvidenceLog(new FakeStationClock());
            log.Reset(Gen);
            return log;
        }
        private static void List(ProfileEvidenceLog log) =>
            log.GlobalListObserved(new[] { "Default", "K5NER" }, ObservationProvenance.RadioReported, Gen);

        [Fact]
        public void Log_FlexLibsAsk_IsAnsweredFirst_AndLabelledAsFlexLibs()
        {
            var log = NewLog();
            log.FlexLibConnectInventoryAsked(Gen);
            Assert.True(log.Snapshot().FlexLibConnectAnswerMayBeOutstanding);
            Assert.Equal(1, log.Snapshot().OutstandingFlexLibConnectAsks);

            List(log);
            var s = log.Snapshot();
            Assert.True(s.GlobalList.AnswersFlexLibConnectAsk);
            Assert.False(s.GlobalList.AnswersOurAsk);
            Assert.Null(s.UnsolicitedGlobalList);
            Assert.False(s.FlexLibConnectAnswerMayBeOutstanding);

            List(log);                                                      // the radio's own
            Assert.NotNull(log.Snapshot().UnsolicitedGlobalList);
        }

        [Fact]
        public void Log_ALocalEcho_NeverAnswersFlexLibsAsk()
        {
            var log = NewLog();
            log.FlexLibConnectInventoryAsked(Gen);
            log.GlobalListObserved(new[] { "Default" }, ObservationProvenance.LocalEcho, Gen);
            Assert.True(log.Snapshot().FlexLibConnectAnswerMayBeOutstanding);
        }

        [Fact]
        public void Log_AnAskOfOursMadeWhileFlexLibsIsOpen_KeepsItAmbiguousUntilBothAreAnswered()
        {
            var log = NewLog();
            log.FlexLibConnectInventoryAsked(Gen);
            log.GlobalInventoryAsked(Gen);
            List(log);
            Assert.True(log.Snapshot().FlexLibConnectAnswerMayBeOutstanding, "one answer for two asks: either may be owed");
            List(log);
            var s = log.Snapshot();
            Assert.False(s.FlexLibConnectAnswerMayBeOutstanding);
            Assert.Equal(0, s.OutstandingGlobalAsks);
            Assert.Null(s.UnsolicitedGlobalList);
        }

        [Fact]
        public void Log_AnAskOfOursMadeAfterFlexLibsWasAnswered_IsNotAmbiguous()
        {
            var log = NewLog();
            log.FlexLibConnectInventoryAsked(Gen);
            List(log);
            log.GlobalInventoryAsked(Gen);
            var s = log.Snapshot();
            Assert.False(s.FlexLibConnectAnswerMayBeOutstanding);
            Assert.Equal(1, s.OutstandingGlobalAsks);
        }

        [Fact]
        public void Log_WithdrawingAnAnsweredAsk_DoesNotWithdrawALaterOne()
        {
            // Codex's two-ask fault edge (round-one re-verify): ask A is
            // answered, ask B goes out, then A's reply task faults. Withdrawing
            // by a shared count took B instead, and B's answer then read as
            // the radio's own list. By id, A's withdrawal changes nothing.
            var log = NewLog();
            long a = log.GlobalInventoryAsked(Gen);
            List(log);                                                      // A's answer
            long b = log.GlobalInventoryAsked(Gen);
            Assert.NotEqual(a, b);
            log.GlobalInventoryAskWithdrawn(Gen, a);                        // A faults late
            Assert.Equal(1, log.Snapshot().OutstandingGlobalAsks);
            List(log);                                                      // B's answer
            Assert.Null(log.Snapshot().UnsolicitedGlobalList);
        }

        [Fact]
        public void Log_WithdrawingAnOpenAsk_RemovesThatAskOnly()
        {
            var log = NewLog();
            long a = log.GlobalInventoryAsked(Gen);
            log.GlobalInventoryAsked(Gen);
            log.GlobalInventoryAskWithdrawn(Gen, a);
            Assert.Equal(1, log.Snapshot().OutstandingGlobalAsks);
        }

        [Fact]
        public void Log_ANewAttempt_ForgetsTheOldAttemptsFlexLibAsk_AndAnOldGenerationRecordsNothing()
        {
            var log = NewLog();
            log.FlexLibConnectInventoryAsked(Gen);
            log.Reset(Gen + 1);
            Assert.False(log.Snapshot().FlexLibConnectAnswerMayBeOutstanding);
            log.FlexLibConnectInventoryAsked(Gen);                          // a stale attempt's record
            Assert.Equal(0, log.Snapshot().OutstandingFlexLibConnectAsks);
            Assert.Equal(0, log.GlobalInventoryAsked(Gen));
        }

        // ── the policy, on hand-built evidence ─────────────────────────────

        [Fact]
        public void Policy_EvidenceThatWouldConfirm_DoesNot_WhenFlexLibsAnswerMayHaveBeenOwedAtTheSend()
        {
            var log = NewLog();
            var station = new StationTracker(new FakeStationClock());
            station.Reset(Gen);
            long seqAtSend = log.Sequence;
            long stationSeqAtSend = station.Sequence;
            var before = station.Snapshot();
            station.OwnSliceAdded(0, "A", 0x1A2B3C4D, 0x40000001, Gen);
            List(log);
            LoadEvidence Evidence(bool owed) => new LoadEvidence
            {
                WantedName = "K5NER",
                AttemptGeneration = Gen,
                SentAtProfileSequence = seqAtSend,
                SentAtStationSequence = stationSeqAtSend,
                StationBefore = before,
                StationNow = station.Snapshot(),
                ProfileNow = log.Snapshot(),
                Acknowledged = true,
                FlexLibConnectAnswerMayBeOutstandingAtSend = owed,
            };

            Assert.Equal(LoadCompletion.Confirmed, LoadCompletionByGlobalListReportPolicy.Instance.Judge(Evidence(owed: false)));   // the control
            Assert.Equal(LoadCompletion.Unconfirmed, LoadCompletionByGlobalListReportPolicy.Instance.Judge(Evidence(owed: true)));
        }
    }

    /// <summary>
    /// The same gap through the PRODUCTION feed on a real FlexBase over a
    /// vendored Radio: the record call FlexBase makes before the vendor's
    /// Connect, then a list status parsed by the vendored code. Reached by
    /// reflection so it compiles on the base, where the method does not
    /// exist and the late list is classed unsolicited (RED on 3c5493fc).
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class FlexLibConnectAskProductionSeamTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(FlexLibConnectAskProductionSeamTests));
        private RigOnVendorRadio _rig;
        public void Dispose()
        {
            _rig?.Dispose();
            _scope.Dispose();
        }

        private static string UniqueSerial() =>
            "9997-" + (1000 + Environment.ProcessId % 9000).ToString("D4") + "-" + (1000 + Environment.TickCount % 9000).ToString("D4") + "-7236";

        private static readonly MethodInfo NoteMethod =
            typeof(FlexBase).GetMethod("NoteFlexLibConnectTimeProfileAsk", BindingFlags.NonPublic | BindingFlags.Instance);

        [Fact]
        public void TheProductionRecord_TakesALateListAsFlexLibsAnswer_AndOnlyTheNextListAsTheRadiosOwn()
        {
            _rig = new RigOnVendorRadio(UniqueSerial());
            // What FlexBase.Connect does immediately before theRadio.Connect.
            NoteMethod?.Invoke(_rig.Rig, new object[] { "Connect" });

            _rig.AddOwnSlice(0, 14.1, "USB");                                   // a restored slice
            _rig.Vendor.Status("profile global list=Default^K5NER");           // FlexLib's late answer, parsed by FlexLib
            Assert.Null(_rig.Profiles.Snapshot().UnsolicitedGlobalList);

            _rig.Vendor.Status("profile global list=Default^K5NER");           // the radio's own re-report
            var own = _rig.Profiles.Snapshot().UnsolicitedGlobalList;
            Assert.NotNull(own);
            Assert.Equal(ObservationProvenance.RadioReported, own.Provenance);

            // A redundant check that the seam the test reaches still exists:
            // without it, the first assertion above would already have failed.
            Assert.NotNull(NoteMethod);
        }

        [Fact]
        public void EveryLiveVendorConnect_IsPrecededByTheRecord()
        {
            // FlexBase.Connect and RetryConnect are the only live callers of
            // the vendor's Connect (a third sits under #if zero). Each must
            // record FlexLib's ask BEFORE the call, so the answer cannot
            // arrive ahead of the record.
            string text = FlexBasePartials.JoinedText();
            const string call = "theRadio.Connect(";
            int live = 0;
            for (int at = text.IndexOf(call, StringComparison.Ordinal); at >= 0; at = text.IndexOf(call, at + 1, StringComparison.Ordinal))
            {
                int ifZero = text.LastIndexOf("#if zero", at, StringComparison.Ordinal);
                int endIf = ifZero < 0 ? -1 : text.IndexOf("#endif", ifZero, StringComparison.Ordinal);
                if (ifZero >= 0 && endIf > at) continue;               // dead code
                live++;
                int windowStart = Math.Max(0, at - 700);
                string before = text.Substring(windowStart, at - windowStart);
                Assert.True(before.Contains("NoteFlexLibConnectTimeProfileAsk(", StringComparison.Ordinal),
                    "a live vendor Connect is not preceded by the record of FlexLib's connect-time profile ask: ..."
                    + before.Substring(Math.Max(0, before.Length - 200)));
            }
            Assert.Equal(2, live);
        }
    }
}
