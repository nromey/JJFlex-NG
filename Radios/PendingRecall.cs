namespace Radios
{
    /// <summary>
    /// What the operator's own choices have done to the radio so far, as marks
    /// a later moment can be compared against: the radio object, the receive
    /// slice, how many mode choices, how many frequency requests and how many
    /// slice moves the app has made. A pending memory recall is the radio's
    /// live request only while none of these has moved since the recall's own
    /// action (Sprint 49 Track N2 repair 2, Codex's R1). The radio is compared
    /// by reference: a disconnect or another radio is a new one.
    ///
    /// The slice is counted as well as read (repair 3, Codex's F2): a move to
    /// another slice and back restores the slice number, so only the count of
    /// moves can see the round trip, as only the count of frequency requests
    /// can see a tune the radio's own report has overwritten.
    /// </summary>
    public sealed record RecallMarks(object? Radio, int ReceiveSlice, int ModeChoices, int FrequencyRequests, int SliceChoices)
    {
        /// <summary>The same radio, the same receive slice, and no choice made since.</summary>
        public bool SameAs(RecallMarks other) =>
            ReferenceEquals(Radio, other.Radio)
            && ReceiveSlice == other.ReceiveSlice
            && ModeChoices == other.ModeChoices
            && FrequencyRequests == other.FrequencyRequests
            && SliceChoices == other.SliceChoices;
    }

    /// <summary>
    /// A memory recall from the Memories window, bound to the operator's action
    /// that made it (#1131; Sprint 49 Track N2 repair 2, Codex's R1): the
    /// frequency and mode it asked for, what the radio reported at that action
    /// (its "before"), when, and the marks taken right after its own request.
    ///
    /// The landing after the window closes runs later, at ApplicationIdle, and
    /// input can run before it. Taking the recall's "before" at the landing let
    /// a tune made in between become the old recall's legitimate starting
    /// point, and a mode choice made in between could not stop the first
    /// sentence. So the recall carries its own before from the action, and any
    /// choice made since (a tune, a mode choice, a slice change, a disconnect)
    /// retires it before the landing uses its values.
    ///
    /// Retirement is permanent (repair 3, Codex's F2): once any check finds the
    /// recall no longer owns the radio, no later check can find that it does,
    /// even if every mark has come back to its old value (a disconnect and the
    /// same radio object again, say). The marks' counts catch a round trip
    /// that happens between two checks; this catches one a check saw.
    /// </summary>
    public sealed class PendingRecall
    {
        /// <summary>
        /// How long a recall counts as the radio's pending answer. A
        /// conservative bound for an orientation sentence (Codex: reasonable),
        /// not a measured hardware response time.
        /// </summary>
        public const long LifetimeMs = 5000;

        public PendingRecall(ulong frequencyHz, string mode, RadioStatusSnapshot before, long at, RecallMarks marks)
        {
            FrequencyHz = frequencyHz;
            Mode = mode ?? "";
            Before = before;
            At = at;
            Marks = marks;
        }

        public ulong FrequencyHz { get; }
        public string Mode { get; }
        /// <summary>What the radio reported at the operator's action, before the recall's request.</summary>
        public RadioStatusSnapshot Before { get; }
        /// <summary>When the action ran (Environment.TickCount64).</summary>
        public long At { get; }
        /// <summary>The marks right after the recall's own request (its own mode choice counted).</summary>
        public RecallMarks Marks { get; }

        private volatile bool _retired;

        /// <summary>
        /// Whether this recall is still the radio's live request: never found
        /// otherwise before, inside its lifetime, and no choice made since. A
        /// false answer retires it for good.
        /// </summary>
        public bool StillOwns(RecallMarks now, long nowTicks)
        {
            if (_retired) return false;
            if (nowTicks - At <= LifetimeMs && Marks.SameAs(now)) return true;
            _retired = true;
            return false;
        }

        /// <summary>
        /// The ask a landing's status names, or null when the recall no longer
        /// owns the radio. Its pending answer asks the same question again at
        /// every later moment, through <paramref name="marksNow"/>.
        /// </summary>
        public StatusAsk? AskIfStillOwned(System.Func<RecallMarks> marksNow, System.Func<long> ticksNow)
        {
            if (!StillOwns(marksNow(), ticksNow())) return null;
            return new StatusAsk(FrequencyHz, Mode, Before, () => StillOwns(marksNow(), ticksNow()));
        }
    }
}
