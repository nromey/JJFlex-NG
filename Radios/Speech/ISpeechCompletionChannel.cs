#nullable enable
using System;

namespace Radios.Speech
{
    /// <summary>
    /// A reader channel that can say what became of an utterance — the
    /// capability the arbiter consults, never a reader name.
    ///
    /// <para><b>A capability, not a reader.</b> <see cref="IScreenReader.DetectedReader"/>
    /// is documented "diagnostics only — never branch behaviour on it", and
    /// that rule holds here even though today exactly one reader can do this.
    /// The channel is an object with a <see cref="CanReportCompletion"/> bit,
    /// following the shape <c>PrismScreenReader.CanReportSpeaking</c> already
    /// uses: <c>PrismScreenReader</c> hands one out when the backend it holds
    /// can answer, and no call site anywhere compares a reader string. When
    /// Prism grows a completion-reporting speak upstream, this interface is
    /// reimplemented over Prism's feature bit and nothing above it
    /// changes.</para>
    ///
    /// <para><b>Nothing waits on the channel; the channel only informs.</b>
    /// The arbiter's standing ruling — no design that works under one screen
    /// reader and silently stalls under another — is intact: where this is
    /// absent the arbiter behaves exactly as before, and where it is present
    /// the only thing that blocks is a dedicated delivery thread that owns
    /// nothing else.</para>
    /// </summary>
    internal interface ISpeechCompletionChannel
    {
        /// <summary>
        /// True while the channel can answer. Goes false for the session when
        /// the reader proves unable to — an interface it does not offer, a
        /// call that hung — and the trace says why.
        /// </summary>
        bool CanReportCompletion { get; }

        /// <summary>Identity for the trace, e.g. "NVDA controller client 2026.2".</summary>
        string Name { get; }

        /// <summary>
        /// Hand one utterance to the reader and BLOCK until it says what
        /// happened. Delivery-thread only: the caller must be a thread that
        /// owns nothing anyone else is waiting on, because NVDA 2024.1 through
        /// 2026.1 can hold this call forever.
        /// </summary>
        /// <param name="text">Plain text. The channel escapes it; callers never build markup.</param>
        /// <param name="ticket">The pump's ticket, woven into every mark name so a mark identifies its utterance.</param>
        /// <param name="onMarkReached">Called on each mark, with the running count, from whichever thread the reader calls back on.</param>
        SpeechOutcome SpeakAndWait(string text, long ticket, Action<int>? onMarkReached);

        /// <summary>
        /// Cut whatever the reader is saying. Callable from ANY thread while
        /// another is inside <see cref="SpeakAndWait"/>; that call then
        /// returns Cancelled with <see cref="SpeechOutcome.CancelledByUs"/>.
        /// </summary>
        void Cancel();

        /// <summary>
        /// The escape for a call that will not return: ask the RPC runtime to
        /// cancel the blocked call on the thread currently inside
        /// <see cref="SpeakAndWait"/>. Returns false when there is no such
        /// thread or the runtime refused.
        /// </summary>
        bool TryCancelBlockedCall();

        /// <summary>
        /// Turn the channel off for the rest of the session with a stated
        /// reason. Used when the reader has shown it cannot be relied on —
        /// nothing degrades to a false Completed, everything degrades to
        /// Unknown and then to the estimate path.
        /// </summary>
        void Disable(string reason);
    }
}
