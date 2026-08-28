#nullable enable
namespace Radios.Speech
{
    /// <summary>
    /// What the backend did with one utterance — the answer to a question this
    /// application could not previously ask.
    ///
    /// <para><b>Why this type exists (#277).</b> <c>IScreenReader.Speak</c>
    /// returned <c>void</c>, and underneath it
    /// <c>PrismNative.prism_backend_speak</c>'s <see cref="PrismError"/> was
    /// discarded at the call site. So speaking into a reader that had gone
    /// returned an error nobody read, <c>Speak</c> returned normally,
    /// <c>EmitCore</c> set <c>reachedBackend = true</c>, and the trace wrote
    /// <c>Spoke</c> for a sentence no human could have heard. Every instrument
    /// we own agreed the words went out.</para>
    ///
    /// <para><b>What that one discarded value cost.</b> It is why a faithful
    /// trace misled four separate capture readings in a single evening, and why
    /// three findings in two days turned out to be the reader-binding bug
    /// wearing a disguise. A dead binding silently disabled every downstream
    /// announcement while every downstream trace claimed success, so the fault
    /// presented as an independent bug in whichever subsystem the operator
    /// happened to be using. Reading the code makes the whole class
    /// self-reporting: one loud line saying the speech layer cannot deliver,
    /// instead of an unbounded family of mysteries.</para>
    ///
    /// <para><b>The tri-state matters and is not an accident.</b> "The backend
    /// refused this" and "there was nothing to attempt" are different facts and
    /// need different reactions — an empty message, a backend that is not up
    /// yet, or braille on a machine with no display are all ordinary and must
    /// not raise an alarm, while a refusal is the fault this type was built to
    /// surface.</para>
    /// </summary>
    public readonly struct SpeechDelivery
    {
        private SpeechDelivery(bool delivered, string? failure)
        {
            Delivered = delivered;
            Failure = failure;
        }

        /// <summary>
        /// True when the backend accepted the text. This — not "we called the
        /// backend" — is what the arbiter's believed-pending ledger must key
        /// on: an utterance the reader refused occupied nothing and, if it
        /// carried an interrupt, flushed nothing either.
        /// </summary>
        public bool Delivered { get; }

        /// <summary>
        /// Why the backend refused, ready to go straight into a trace line, or
        /// null. Null with <see cref="Delivered"/> false means nothing was
        /// attempted, which is not a fault — see <see cref="NotAttempted"/>.
        /// </summary>
        public string? Failure { get; }

        /// <summary>True when the backend actively refused the text.</summary>
        public bool Refused => !Delivered && Failure != null;

        /// <summary>The backend took it.</summary>
        public static SpeechDelivery Accepted => new SpeechDelivery(true, null);

        /// <summary>
        /// Nothing was handed over: no backend yet, an empty message, or a
        /// capability the backend does not have (braille without a display).
        /// Deliberately NOT a failure — reporting these would train a reader to
        /// ignore the ones that matter.
        /// </summary>
        public static SpeechDelivery NotAttempted => new SpeechDelivery(false, null);

        /// <summary>
        /// The backend was asked and said no. <paramref name="reason"/> is
        /// quoted verbatim into the trace, so it should name the call, the
        /// reader and the error rather than merely reporting a refusal.
        /// </summary>
        public static SpeechDelivery Failed(string reason) =>
            new SpeechDelivery(false, reason);
    }
}
