namespace Radios.Speech
{
    /// <summary>
    /// What KIND of utterance this is — the caller's intent, not a mechanism.
    ///
    /// **Why this replaces a bool.** 429 of the application's 664 speech call
    /// sites passed <c>interrupt: true</c>. That is not 429 decisions; several
    /// whole files were 100% interrupt-true, which is what copy-paste looks
    /// like. The reason is that <c>interrupt</c> asked every call site a
    /// question no call site can answer: "is something more important being
    /// spoken right now?" A handler at the bottom of a dialog cannot know. So
    /// the safe-looking answer — yes, cut in — became the default, and the
    /// application talked over itself.
    ///
    /// An intent is answerable locally. "Am I one of a series?" and "did the
    /// operator just press a key?" are questions the call site alone knows.
    /// Arbitration then happens once, centrally, where the pending state is.
    ///
    /// Four independent hand-rolled workarounds were found for the missing
    /// concept — three <c>Task.Delay</c> calls in the SmartLink flow and a
    /// 2-second sleep guarding the welcome message — each written by someone
    /// who diagnosed the symptom correctly and had no shared mechanism to fix
    /// it with.
    /// </summary>
    public enum SpeechIntent
    {
        /// <summary>
        /// A discrete thing the operator just did, whose result supersedes
        /// anything pending. Mode change, band change, a toggle flipping.
        ///
        /// Test: if the previous utterance is cut off mid-word, has the
        /// operator lost anything? If no — Interrupt.
        /// </summary>
        Interrupt = 0,

        /// <summary>
        /// One part of a series where every part matters and order carries
        /// meaning. Startup, the connect sequence, an error followed by its
        /// explanation.
        ///
        /// Nearly free: screen readers already queue. Passing "do not
        /// interrupt" puts our text in the reader's own queue, which is the
        /// queue we were destroying 429 times.
        /// </summary>
        Queue,

        /// <summary>
        /// A repeated same-kind utterance where only the newest value is worth
        /// hearing. Riding a slider, sweeping the VFO, a meter readout.
        ///
        /// Needs a coalescing key so the handler knows which pending utterance
        /// this one REPLACES. Same key replaces; different keys do not.
        ///
        /// Unlike the other three this cannot be expressed by the interrupt
        /// flag at all, and it is the reason the enum has to exist rather than
        /// the bool simply being renamed. Coalescing happens BEFORE emission:
        /// once text is handed to a screen reader it cannot be retracted.
        /// </summary>
        Latest,

        /// <summary>
        /// Transmit safety. Cuts current speech AND discards what is queued, so
        /// the warning is the last thing the operator hears.
        ///
        /// Plain Interrupt is not sufficient here: it stops what is speaking
        /// but leaves the queue intact, so stale readouts play out on top of a
        /// warning that the radio is still transmitting. Reserved for the
        /// handful of sites where that matters — see PttSafetyController.
        /// </summary>
        Urgent,
    }
}
