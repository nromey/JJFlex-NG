#nullable enable
using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    // ────────────────────────────────────────────────────────────────
    //  #291 — the real cause of the reader-binding failure: a working
    //  reader is never displaced, so the re-acquire edge is thrown away.
    //
    //  The rule lived only in a comment, above a line that implemented a
    //  DIFFERENT rule. "Do not displace a working reader" is defensible; what
    //  the code did was refuse to NOTICE a new reader, and the two differ only
    //  later, when the held reader dies. These tests exist because that
    //  divergence was previously unassertable — there was no seam between the
    //  policy and the P/Invoke carrying it out.
    //
    //  The sequence that could not survive the old line, and which
    //  ReaderBindingSurvivesJawsStartingUnderNvda below walks step by step:
    //  start under NVDA, launch JAWS while NVDA still runs, quit NVDA. JAWS
    //  must take over.
    // ────────────────────────────────────────────────────────────────
    public class ReaderAvailabilityEdgeTests
    {
        [Fact]
        public void AReaderArriving_WhileOursIsWorking_IsIgnored()
        {
            // The policy that is genuinely right and is deliberately kept: the
            // operator's channel stands. Deleting this guard would have "fixed"
            // #291 by rebinding mid-sentence every time another reader started.
            var action = ReaderAvailabilityEdge.Decide(
                holdingControllerReader: true,
                heldReaderLost: false,
                isHeldReader: false,
                nowAvailable: true);

            Assert.Equal(ReaderAvailabilityAction.Ignore, action);
        }

        [Fact]
        public void LosingTheReaderWeHold_SweepsForOneAlreadyRunning()
        {
            // THE FIX. The old code marked the binding dead and then waited for
            // a RISE — which a reader that is already running can never
            // produce. HoldAndSweep is the instruction to go and look.
            var action = ReaderAvailabilityEdge.Decide(
                holdingControllerReader: true,
                heldReaderLost: false,
                isHeldReader: true,
                nowAvailable: false);

            Assert.Equal(ReaderAvailabilityAction.HoldAndSweep, action);
        }

        [Fact]
        public void LosingAReaderWeDoNotHold_ChangesNothing()
        {
            // Losing JAWS matters not at all while we are speaking through NVDA.
            var action = ReaderAvailabilityEdge.Decide(
                holdingControllerReader: true,
                heldReaderLost: false,
                isHeldReader: false,
                nowAvailable: false);

            Assert.Equal(ReaderAvailabilityAction.Ignore, action);
        }

        [Fact]
        public void LosingTheHeldReaderTwice_DoesNotSweepTwice()
        {
            // Prism's enumerator can report the same absence again. A second
            // "it is gone" for a reader already known to be gone is not a new
            // edge, and treating it as one would start a second sweep racing
            // the first.
            var action = ReaderAvailabilityEdge.Decide(
                holdingControllerReader: true,
                heldReaderLost: true,
                isHeldReader: true,
                nowAvailable: false);

            Assert.Equal(ReaderAvailabilityAction.Ignore, action);
        }

        [Fact]
        public void AReaderArriving_WhileOurBindingIsDead_IsTaken()
        {
            // The rescue path. The tier still reads "screen reader" here — the
            // handle exists, the calls just go nowhere — so the lost flag, not
            // the tier, is what makes this a rescue rather than a displacement.
            var action = ReaderAvailabilityEdge.Decide(
                holdingControllerReader: true,
                heldReaderLost: true,
                isHeldReader: false,
                nowAvailable: true);

            Assert.Equal(ReaderAvailabilityAction.Reacquire, action);
        }

        [Fact]
        public void AReaderArriving_WhileWeAreOnASynthesiserOrUia_IsTaken()
        {
            // #167's original case: the operator's NVDA came up a moment after
            // we did, and without this they had a raw synthesiser for the life
            // of the process with nothing anywhere saying why.
            var action = ReaderAvailabilityEdge.Decide(
                holdingControllerReader: false,
                heldReaderLost: false,
                isHeldReader: false,
                nowAvailable: true);

            Assert.Equal(ReaderAvailabilityAction.Reacquire, action);
        }

        [Fact]
        public void ReaderBindingSurvivesJawsStartingUnderNvda()
        {
            // THE FIELD SEQUENCE, as a walk. This is the one the old line could
            // not survive, and the one to press by hand on a real build: start
            // under NVDA, launch JAWS while NVDA still runs, quit NVDA.

            // 1. Bound to NVDA and happy. JAWS starts.
            var onJawsArriving = ReaderAvailabilityEdge.Decide(
                holdingControllerReader: true, heldReaderLost: false,
                isHeldReader: false, nowAvailable: true);
            Assert.Equal(ReaderAvailabilityAction.Ignore, onJawsArriving);

            // 2. NVDA exits. THIS is the step that used to end the story: the
            //    binding was marked dead and everything then waited for a rise
            //    from a JAWS that was already up and would never rise again.
            var onNvdaLeaving = ReaderAvailabilityEdge.Decide(
                holdingControllerReader: true, heldReaderLost: false,
                isHeldReader: true, nowAvailable: false);
            Assert.Equal(ReaderAvailabilityAction.HoldAndSweep, onNvdaLeaving);

            // 3. And if a rise DOES arrive after that — NVDA restarting, or
            //    another reader starting later — it is now a rescue and is
            //    taken, rather than being discarded as a displacement.
            var afterLoss = ReaderAvailabilityEdge.Decide(
                holdingControllerReader: true, heldReaderLost: true,
                isHeldReader: false, nowAvailable: true);
            Assert.Equal(ReaderAvailabilityAction.Reacquire, afterLoss);
        }
    }
}
