using System;

namespace Radios.ChainChecks
{
    /// <summary>
    /// The one question to ask an operator before they send a transmit
    /// diagnosis to their radio's maker — and the three quite different things
    /// their answer means.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Noel, 2026-08-25:</b> "I'm not sure we should put that this happens
    /// in SmartSDR — that should be placed by the operator. Either that, or we
    /// add a checkbox that prompts them: before sending this diagnosis, did you
    /// test, with the same result, the transmission with SmartSDR?"
    /// </para>
    /// <para>
    /// A prompt beats a free-text field for three reasons. It is a specific
    /// question with a specific answer, so nobody has to compose a sentence
    /// they are unsure of. It arrives at the moment of sending, which is when
    /// it matters. And it TEACHES — plenty of operators would never think to
    /// try the vendor's own client first, and being asked is how they find out
    /// that it is the thing which makes their report credible.
    /// </para>
    /// <para>
    /// <b>The third answer is the one worth building for.</b> If the fault
    /// does NOT happen in SmartSDR, the operator should not be writing to
    /// FlexRadio at all — the fault is ours, and the useful thing they can do
    /// is tell US. A prompt that only gathered a fact would let them send a
    /// support ticket about a bug in this application, which wastes their time,
    /// wastes a support engineer's, and spends credibility that the next
    /// genuine report will need.
    /// </para>
    /// <para>
    /// Nothing here ever writes the claim on the operator's behalf. We cannot
    /// know it, and an unverifiable assertion in our voice would poison the one
    /// section a vendor is most likely to act on. See #217.
    /// </para>
    /// </remarks>
    public static class SmartSdrCrossCheck
    {
        /// <summary>What the operator said when asked.</summary>
        public enum Answer
        {
            /// <summary>Not asked yet. The starting state; never assume from it.</summary>
            NotAsked,
            /// <summary>Asked, and they have not tried the vendor's own client.</summary>
            NotTested,
            /// <summary>Tried it, and the fault is the same there.</summary>
            SameInSmartSdr,
            /// <summary>Tried it, and it WORKS there. The fault is ours.</summary>
            WorksInSmartSdr,
        }

        /// <summary>The question, worded so a yes or a no both mean something.</summary>
        public const string Question =
            "Before you send this, did you try the same transmission in SmartSDR?";

        /// <summary>
        /// The line that goes at the top of the evidence block, or empty for
        /// none.
        /// </summary>
        /// <remarks>
        /// NotTested produces an honest line rather than silence. A block that
        /// simply omits the section reads as though the question never arose;
        /// saying "not tried" tells a support engineer exactly how much weight
        /// to give the rest, which is the same courtesy the chain check already
        /// extends by reporting what it could not see.
        /// </remarks>
        public static string EvidenceLine(Answer answer) => answer switch
        {
            Answer.SameInSmartSdr =>
                "The operator reports the same fault in SmartSDR, on this radio, in the same session.",
            Answer.NotTested =>
                "The operator has NOT tried this in SmartSDR, so it is not yet known whether the "
                + "fault is specific to any one client.",
            _ => "",
        };

        /// <summary>
        /// Whether sending this to the radio's maker is the right next action.
        /// </summary>
        public static bool WorthSendingToFlex(Answer answer) =>
            answer != Answer.WorksInSmartSdr;

        /// <summary>
        /// What to tell the operator once they have answered. Plain, and with
        /// a next step in every branch.
        /// </summary>
        /// <remarks>
        /// This is our own user, so it says what it thinks — the reticence rule
        /// governs what we put in the VENDOR's document, not how we talk to the
        /// person in front of us.
        /// </remarks>
        public static string OperatorGuidance(Answer answer) => answer switch
        {
            Answer.SameInSmartSdr =>
                "Good — that is the single most useful sentence in the whole report. It moves this "
                + "from a question about JJ Flexible to a question about the radio, and it is at "
                + "the top of what you are about to send.",

            Answer.WorksInSmartSdr =>
                "Then please do NOT send this to FlexRadio — the fault is in JJ Flexible, not in "
                + "your radio, and it is ours to fix. Send it to us instead. Working in SmartSDR "
                + "and not here is the most useful thing you could have found, and it points "
                + "straight at our software.",

            Answer.NotTested =>
                "That is fine, and the report still stands on its measurements. But trying the same "
                + "transmission in SmartSDR is the quickest way to make it far more convincing — "
                + "if it fails there too, the question stops being about which software you run.",

            _ =>
                "Answer this before sending, so the report says how much has been ruled out.",
        };
    }
}
