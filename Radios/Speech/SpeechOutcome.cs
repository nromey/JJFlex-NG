#nullable enable
using System;

namespace Radios.Speech
{
    /// <summary>What became of one utterance handed to a reader that can say.</summary>
    public enum SpeechOutcomeKind
    {
        /// <summary>The reader reported it spoken to the end. The ONLY value that means "heard".</summary>
        Completed,

        /// <summary>
        /// The reader reported it cut off. <see cref="SpeechOutcome.MarksReached"/>
        /// says how far it got, and <see cref="SpeechOutcome.CancelledByUs"/>
        /// whether the cut was our own interrupt or something else — the
        /// operator's keystroke, a focus change, another application.
        /// </summary>
        Cancelled,

        /// <summary>
        /// Nobody can say. The call timed out, the channel was not there, the
        /// reader refused the text, or our own escape cancelled the RPC. An
        /// Unknown entry takes the estimate path the arbiter used before any
        /// reader could answer, and is traced as unknown so a capture reading
        /// can see where the ledger was guessing.
        /// </summary>
        Unknown,
    }

    /// <summary>Why an outcome is <see cref="SpeechOutcomeKind.Unknown"/>.</summary>
    public enum SpeechUnknownReason
    {
        /// <summary>Not Unknown. Present so a Completed or Cancelled outcome carries no accidental reason.</summary>
        None,

        /// <summary>The synchronous call did not return inside its deadline. NVDA 2024.1 through 2026.1 can hang it forever.</summary>
        Timeout,

        /// <summary>No completion channel: DLL missing, wrong client, NVDA too old for the interface, or NVDA gone mid-call.</summary>
        ChannelAbsent,

        /// <summary>The reader refused the text — NVDA is in sleep mode for the focused application (ACCESS_DENIED).</summary>
        Refused,

        /// <summary>The reader refused the SSML as malformed (INVALID_PARAMETER). Our escaping defect; the text was re-sent through the plain path.</summary>
        InvalidSsml,

        /// <summary>Our own escape cancelled the blocked RPC (RPC_S_CALL_CANCELLED). The reader may or may not have finished.</summary>
        Escaped,

        /// <summary>A return code this code does not classify. The detail carries it.</summary>
        Other,
    }

    /// <summary>
    /// The tri-state answer to "was that heard?" — Completed, Cancelled at a
    /// known point, or Unknown for a stated reason.
    ///
    /// <para><b>Why a type and not a bool (#521).</b> The salvage ledger
    /// recorded that five utterances were EMITTED at one tick and had no way
    /// to learn that the reader spent the next ten seconds saying four of
    /// them. Any interrupt therefore made the entire backlog look unheard,
    /// and the rescue built to stop announcements going missing became the
    /// thing that repeated them — the operator disconnected and was told he
    /// was connected. NVDA can answer the question; this is the shape of the
    /// answer.</para>
    ///
    /// <para><b>Unknown is never Completed, and the type makes that
    /// unrepresentable to lose.</b> There is no "spoken" flag anywhere: the
    /// only way to say heard is <see cref="Kind"/> equal to
    /// <see cref="SpeechOutcomeKind.Completed"/>, and an Unknown outcome must
    /// name its reason. A system that cannot tell spoken from unknown is
    /// worse than one that never asked, because it would prune speech the
    /// operator never received while the record said everything was
    /// fine.</para>
    /// </summary>
    public readonly struct SpeechOutcome
    {
        private SpeechOutcome(SpeechOutcomeKind kind, int marksReached, int markCount,
            bool cancelledByUs, SpeechUnknownReason reason, string? detail, int elapsedMs)
        {
            Kind = kind;
            MarksReached = marksReached;
            MarkCount = markCount;
            CancelledByUs = cancelledByUs;
            UnknownReason = reason;
            Detail = detail;
            ElapsedMs = elapsedMs;
        }

        public SpeechOutcomeKind Kind { get; }

        /// <summary>
        /// How many <c>&lt;mark&gt;</c>s the synthesiser reached. One mark
        /// precedes every word, so this is the number of words the reader
        /// got to. For a Completed outcome it equals <see cref="MarkCount"/>.
        /// </summary>
        public int MarksReached { get; }

        /// <summary>How many marks the utterance carried — its word count.</summary>
        public int MarkCount { get; }

        /// <summary>
        /// For Cancelled: true when the cut was OUR cancel (an interrupt, a
        /// Silence, an Urgent), false when something outside this application
        /// cancelled NVDA — the operator's own keystroke does that on every
        /// press. The arbiter's interrupt path has already accounted for the
        /// first kind; the second is new information.
        /// </summary>
        public bool CancelledByUs { get; }

        /// <summary>Why Unknown, or <see cref="SpeechUnknownReason.None"/> otherwise.</summary>
        public SpeechUnknownReason UnknownReason { get; }

        /// <summary>Free text for the trace: the return code, the fallback taken, the escape stage.</summary>
        public string? Detail { get; }

        /// <summary>Wall time from hand-over to the reader's answer.</summary>
        public int ElapsedMs { get; }

        /// <summary>The reader said it finished. The one and only "heard".</summary>
        public bool WasHeard => Kind == SpeechOutcomeKind.Completed;

        public static SpeechOutcome Completed(int marksReached, int markCount, int elapsedMs) =>
            new SpeechOutcome(SpeechOutcomeKind.Completed, marksReached, markCount,
                cancelledByUs: false, SpeechUnknownReason.None, null, elapsedMs);

        public static SpeechOutcome Cancelled(int marksReached, int markCount, bool byUs, int elapsedMs) =>
            new SpeechOutcome(SpeechOutcomeKind.Cancelled, marksReached, markCount,
                byUs, SpeechUnknownReason.None, null, elapsedMs);

        /// <summary>An Unknown outcome must say why; None is refused so no caller can leave it blank.</summary>
        public static SpeechOutcome Unknown(SpeechUnknownReason reason, string? detail, int elapsedMs,
            int marksReached = 0, int markCount = 0)
        {
            if (reason == SpeechUnknownReason.None)
                throw new ArgumentException("An unknown outcome must carry a reason.", nameof(reason));
            return new SpeechOutcome(SpeechOutcomeKind.Unknown, marksReached, markCount,
                cancelledByUs: false, reason, detail, elapsedMs);
        }

        /// <summary>The same outcome with a longer detail — used when a fallback path was taken afterwards.</summary>
        public SpeechOutcome WithDetail(string detail) =>
            new SpeechOutcome(Kind, MarksReached, MarkCount, CancelledByUs, UnknownReason, detail, ElapsedMs);

        /// <summary>One line for the trace, assembled here so every caller says it the same way.</summary>
        public override string ToString()
        {
            switch (Kind)
            {
                case SpeechOutcomeKind.Completed:
                    return $"heard to the end in {ElapsedMs} ms ({MarkCount} word(s))";
                case SpeechOutcomeKind.Cancelled:
                    return $"cancelled at word {MarksReached} of {MarkCount} after {ElapsedMs} ms, "
                        + (CancelledByUs ? "by us" : "NOT by us — the operator's key or a focus change");
                default:
                    return $"UNKNOWN ({UnknownReason}) after {ElapsedMs} ms"
                        + (MarkCount > 0 ? $", {MarksReached} of {MarkCount} word(s) reached" : string.Empty)
                        + (string.IsNullOrEmpty(Detail) ? string.Empty : $": {Detail}");
            }
        }
    }
}
