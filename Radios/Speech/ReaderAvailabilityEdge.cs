namespace Radios.Speech
{
    /// <summary>What to do when a controller reader's availability changes.</summary>
    public enum ReaderAvailabilityAction
    {
        /// <summary>Nothing. Either it is not our reader, or ours is working.</summary>
        Ignore,

        /// <summary>
        /// The reader WE hold has gone. Keep the handle — calls to a dead
        /// reader fail harmlessly, and tearing the channel down would race
        /// every Speak in flight — mark the binding dead, and then go LOOK for
        /// a reader that is already running.
        /// </summary>
        HoldAndSweep,

        /// <summary>A reader is available and our binding is not working. Take it.</summary>
        Reacquire,
    }

    /// <summary>
    /// When the speech channel should move, on a screen reader appearing or
    /// disappearing. Pure policy: observations in, a decision out — the same
    /// shape as <c>ScreenReaderWatch</c>, and for the same reason.
    ///
    /// <para><b>The bug this exists to make assertable (#291).</b> The rule was
    /// written only in prose, in a comment above the line that implemented it:
    /// <i>"A working controller reader is never displaced: if JAWS starts while
    /// NVDA is speaking for us, the operator's channel stands."</i> That policy
    /// is defensible. The code did something else — it refused to NOTICE a new
    /// reader, which is not the same thing, and the difference only shows up
    /// later.</para>
    ///
    /// <para>The #167 re-acquire fires on the RISING EDGE of a reader becoming
    /// available. Start JAWS while NVDA is still running and that rise is
    /// discarded. NVDA then exits, the binding is marked dead — and nothing
    /// re-checks, because JAWS is ALREADY available and will never rise again.
    /// The application talks to a dead NVDA forever. It explains all three
    /// field observations: total silence one way round, "kind of works" the
    /// other where ordering happens to favour it, and convergence after several
    /// swaps, when an edge finally lands while the binding is already marked
    /// dead.</para>
    ///
    /// <para><b>The comment and the code could not disagree in a way any test
    /// could see</b>, because there was no seam between the policy and the
    /// P/Invoke that carried it out. That is why the fix is a decider and not
    /// just five corrected lines: the next person to change this can be told,
    /// by a failing test, that they have re-broken it.</para>
    ///
    /// <para><b>What is deliberately preserved.</b> A reader that merely
    /// RESTARTS must not cause a rebind — NVDA restarts are routine for the
    /// people who rely on it. That is why loss produces
    /// <see cref="ReaderAvailabilityAction.HoldAndSweep"/> rather than an
    /// immediate switch: the caller waits before adopting a different reader,
    /// and a returning reader wins the race on its own rise. The same intent
    /// the watchdog encodes as its longer no-reader settle.</para>
    /// </summary>
    public static class ReaderAvailabilityEdge
    {
        /// <param name="holdingControllerReader">
        /// True when the channel is currently a controller reader (as opposed
        /// to UI Automation or a raw synthesiser).
        /// </param>
        /// <param name="heldReaderLost">
        /// True when the reader we hold has already been observed to go away.
        /// The tier still reads "screen reader" in that state — the handle
        /// exists, the calls just go nowhere — so this flag, not the tier, is
        /// what says the binding is dead.
        /// </param>
        /// <param name="isHeldReader">
        /// True when the reader whose availability changed is the one we are
        /// bound to. Losing JAWS matters not at all while we speak through NVDA.
        /// </param>
        /// <param name="nowAvailable">The direction of the change.</param>
        public static ReaderAvailabilityAction Decide(
            bool holdingControllerReader,
            bool heldReaderLost,
            bool isHeldReader,
            bool nowAvailable)
        {
            if (!nowAvailable)
            {
                // Only the loss of the reader WE hold matters, and only while
                // we still believe the binding is good — a second "it is gone"
                // for a reader already known to be gone is not a new edge.
                return holdingControllerReader && isHeldReader && !heldReaderLost
                    ? ReaderAvailabilityAction.HoldAndSweep
                    : ReaderAvailabilityAction.Ignore;
            }

            // A WORKING controller reader is never displaced. This is the
            // policy the old comment described, and it is kept exactly: a
            // reader arriving while ours is fine changes nothing.
            if (holdingControllerReader && !heldReaderLost)
                return ReaderAvailabilityAction.Ignore;

            // Our channel is a synthesiser, UI Automation, or a dead binding.
            // Any controller reader is an improvement on all three.
            return ReaderAvailabilityAction.Reacquire;
        }
    }
}
