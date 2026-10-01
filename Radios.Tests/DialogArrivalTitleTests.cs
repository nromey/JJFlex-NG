using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The pending-title ownership rule behind every dialog's close hook
    /// (#551, Sol's Track J review, closed on Track K and tested here).
    /// </summary>
    /// <remarks>
    /// The regression this pins: the outgoing search window, closed only
    /// AFTER the picker has rendered, used to withdraw the picker's
    /// still-unspoken title because the subject is global and the hook did
    /// not ask whose title was pending. The sequence is scripted exactly as
    /// <c>WindowHandoff.CloseAfterSuccessorShown</c> produces it, with plain
    /// objects standing in for the two windows: the rule is about identity and
    /// order, and nothing in it is visual, so no window is constructed and the
    /// desk is not touched.
    /// </remarks>
    public class DialogArrivalTitleTests
    {
        [Fact]
        public void TheOutgoingSearchWindow_ClosingAfterThePickerSpoke_DoesNotWithdrawThePickersTitle()
        {
            var claim = new ArrivalTitleClaim();
            var search = new object();
            var picker = new object();

            // Loaded, in handoff order: the search window spoke its title
            // first; then the picker rendered and spoke its own.
            claim.Claim(search);
            claim.Claim(picker);

            // CloseAfterSuccessorShown now closes the search window. Its hook
            // must find that the pending title is not its own and withdraw
            // NOTHING — that false is the whole fix.
            Assert.False(claim.Release(search));
            Assert.True(claim.IsHeldBy(picker));
        }

        [Fact]
        public void ThePickerClosingLater_DoesWithdrawItsOwnTitle()
        {
            // The positive control: the hook still withdraws a title when the
            // closing window is the one that queued it. Without this the test
            // above would also pass for a hook that never withdraws anything.
            var claim = new ArrivalTitleClaim();
            var search = new object();
            var picker = new object();
            claim.Claim(search);
            claim.Claim(picker);
            claim.Release(search);

            Assert.True(claim.Release(picker));
            Assert.True(claim.IsEmpty);
        }

        [Fact]
        public void AWindowThatNeverSpokeATitle_CanNotWithdrawAnybodyElses()
        {
            var claim = new ArrivalTitleClaim();
            var spoke = new object();
            var silent = new object();
            claim.Claim(spoke);

            Assert.False(claim.Release(silent));
            Assert.True(claim.IsHeldBy(spoke));
        }

        [Fact]
        public void ReleasingTwice_IsHarmless_AndTheSecondWithdrawsNothing()
        {
            var claim = new ArrivalTitleClaim();
            var w = new object();
            claim.Claim(w);
            Assert.True(claim.Release(w));
            Assert.False(claim.Release(w));
        }
    }
}
