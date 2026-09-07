#nullable enable
namespace Radios.Speech
{
    /// <summary>
    /// What the sink did with one utterance: whether it reached the reader
    /// (or the paced delivery standing in front of it), and — when a
    /// completion channel is carrying it — the ticket by which the reader's
    /// answer will come back.
    ///
    /// <para><b>Why a struct and not the bool it replaces.</b> The sink
    /// returned <c>bool</c> and the arbiter's ledger keyed on it, which was
    /// right and stays right: <see cref="Reached"/> is that bool. What #521
    /// adds is a second fact the ledger needs — the identity under which a
    /// later <c>SpeechOutcome</c> will name this utterance — and a ticket of
    /// 0 says "untracked: nobody will ever report on this one, estimate as
    /// before". The implicit conversion from <c>bool</c> keeps every existing
    /// sink — production and test — compiling and meaning what it meant.</para>
    /// </summary>
    internal readonly struct SpeechHandoff
    {
        public SpeechHandoff(bool reached, long ticket)
        {
            Reached = reached;
            Ticket = reached ? ticket : 0;
        }

        /// <summary>The text was handed on: to the reader directly, or to the paced delivery that will hand it to the reader in turn.</summary>
        public bool Reached { get; }

        /// <summary>
        /// Non-zero when a completion channel will report what became of
        /// this utterance under this number. Zero means the estimate path —
        /// exactly the pre-#521 behaviour.
        /// </summary>
        public long Ticket { get; }

        /// <summary>True when a completion outcome is coming for this utterance.</summary>
        public bool Tracked => Reached && Ticket != 0;

        public static SpeechHandoff NotReached => new SpeechHandoff(false, 0);
        public static SpeechHandoff Untracked => new SpeechHandoff(true, 0);
        public static SpeechHandoff TrackedAs(long ticket) => new SpeechHandoff(true, ticket);

        public static implicit operator SpeechHandoff(bool reached) => new SpeechHandoff(reached, 0);
    }
}
