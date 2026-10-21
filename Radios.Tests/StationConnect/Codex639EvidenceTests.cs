using System.Linq;
using Radios;
using Radios.StationConnect;
using Xunit;

namespace Radios.Tests.StationConnect
{
    // Written by Codex (gpt-6-astra) reviewing #639's first round, 2026-10-20,
    // as review evidence; compiled and run by the implementing lane. RED on
    // 8c990484: LateInventoryAnswerMustNotConfirmAfterOnlyFirstRestoredSlice
    // failed on wholeLoadDelivered (the coordinator returned RestoredConfirmed
    // after the late list and the FIRST slice), the three controls passed.
    //
    // The finding, in Codex's words: ReadRadioProfileList can time out with
    // cached names and Reported=true. ReadProfileTypeState ignores
    // FreshAnswer, so the original info answer can arrive after
    // SendGlobalLoad. The tracker cannot distinguish its list observation
    // from a load's list report.
    //
    // Note what the first test models: a radio-reported list on the first
    // wait with NO ask of ours recorded, which is the shape of FlexLib's
    // connect-time answer arriving late; the shape where the list answers OUR
    // outstanding ask is in LoadCompletionPolicyTests. Both must leave the
    // coordinator waiting for the radio's own list after the newest slice.
    //
    // Repair round two (2026-10-20): production now RECORDS FlexLib's
    // connect-time ask, so this unrecorded list models a list of unknown
    // origin. The shape Codex's re-verify named, FlexLib's late answer AFTER
    // the first restored slice, is in FlexLibConnectAskTests.
    public class Codex639EvidenceTests
    {
        [Fact]
        public void LateInventoryAnswerMustNotConfirmAfterOnlyFirstRestoredSlice()
        {
            var h = new StationHarness(StationPolicies.Defaults());
            h.ArrangeOwnerReconnect();
            bool wholeLoadDelivered = false;
            h.Waiter
                .Then(() => h.RadioReportsGlobalList(h.Port.InventoryNames.ToArray()))
                .Then(() => h.OwnSliceArrives())
                .Then(() =>
                {
                    h.OwnSliceArrives();
                    h.OwnSliceArrives();
                    h.RadioReportsGlobalList(h.Port.InventoryNames.ToArray());
                    wholeLoadDelivered = true;
                });

            var result = h.Run();

            Assert.Equal(StationOutcome.RestoredConfirmed, result.Outcome);
            Assert.True(wholeLoadDelivered,
                "The coordinator reported a completed restore before the remaining slices and load report.");
            Assert.Equal(3, result.OwnSlicesAtEnd);
        }

        [Fact]
        public void ControlWithoutLateInventoryAnswerWaitsForWholeLoad()
        {
            var h = new StationHarness(StationPolicies.Defaults());
            h.ArrangeOwnerReconnect();
            bool wholeLoadDelivered = false;
            h.Waiter
                .Then(() => h.OwnSliceArrives())
                .Then(() =>
                {
                    h.OwnSliceArrives();
                    h.OwnSliceArrives();
                    h.RadioReportsGlobalList(h.Port.InventoryNames.ToArray());
                    wholeLoadDelivered = true;
                });

            var result = h.Run();

            Assert.Equal(StationOutcome.RestoredConfirmed, result.Outcome);
            Assert.True(wholeLoadDelivered);
            Assert.Equal(3, result.OwnSlicesAtEnd);
        }

        [Fact]
        public void ControlLateInventoryWithoutAnySliceRemainsUnconfirmed()
        {
            var h = new StationHarness(StationPolicies.Defaults());
            h.ArrangeOwnerReconnect();
            h.Waiter.Then(() => h.RadioReportsGlobalList(h.Port.InventoryNames.ToArray()));

            var result = h.Run();

            Assert.Equal(StationOutcome.Unconfirmed, result.Outcome);
            Assert.True(result.LoadOutstanding);
            Assert.False(result.StationEstablished);
        }

        [Fact]
        public void ReusedSliceIndexRemainsUnconfirmedThroughWholePhase()
        {
            var h = new StationHarness(StationPolicies.Defaults());
            h.ArrangeOwnerReconnect();
            int index = h.OwnSliceArrives();
            h.Port.Selection = "Default";
            h.Port.OnGlobalLoadSent = _ =>
            {
                h.OwnSliceRemoved(index);
                h.Station.OwnSliceAdded(index, "A", StationHarness.OurHandle, 0x40000001, h.Gen);
                h.RadioReportsGlobalList(h.Port.InventoryNames.ToArray());
            };
            long startedAt = h.Clock.NowMs;

            var result = h.Coordinator().RunOperatorRequestedLoad();

            Assert.Equal(StationOutcome.Unconfirmed, result.Outcome);
            Assert.True(result.LoadOutstanding);
            Assert.True(h.Clock.NowMs - startedAt >= h.Deadlines.StationPhaseMs);
            Assert.Equal(1, result.OwnSlicesAtEnd);
        }
    }
}
