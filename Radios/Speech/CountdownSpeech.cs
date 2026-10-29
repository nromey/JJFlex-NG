#nullable enable
namespace Radios.Speech
{
    /// <summary>
    /// The spoken half of the countdown before a check keys (#835): how its
    /// words reach the reader, whether any are still owed, and taking them back
    /// when the count is called off. One policy, used by the live countdown
    /// through <see cref="ScreenReaderOutput.CountdownSpeech"/> and by tests on
    /// an arbiter of their own, so a test exercises the same handoff the
    /// operator hears.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Codex round 1, 2026-10-28, two blockers this answers.</b> The numbers
    /// were queued on the pips' clock with nothing listening for whether they
    /// had been said, so a busy reader delayed them while the count, and the
    /// key, ran on time; and they shared a subject without being additive, so
    /// each new number superseded, and the pump withdrew, an unsaid earlier
    /// one. And a cancelled count's words stayed queued, because the
    /// still-valid test the countdown supplied is honoured only for Urgent
    /// speech.
    /// </para>
    /// <para>
    /// <b>So:</b> each piece is ADDITIVE on
    /// <see cref="SpeechSubject.CheckCountdown"/> (a number extends the count,
    /// it does not restate it); <see cref="Owed"/> asks the arbiter whether any
    /// piece is still unstarted, which the countdown waits on before each beat
    /// and before the key; and <see cref="Withdraw"/> supersedes the subject,
    /// which takes every unsent piece back out of the delivery pump.
    /// </para>
    /// <para>
    /// Queued, never Urgent or interrupting: an interrupt would let each number
    /// cut the one before, and Urgent would discard the operator's other speech.
    /// Critical, because with sounds off these words are the only warning that
    /// the radio is about to key.
    /// </para>
    /// </remarks>
    public sealed class CountdownSpeech
    {
        private readonly SpeechArbiter _arbiter;

        internal CountdownSpeech(SpeechArbiter arbiter) => _arbiter = arbiter;

        /// <summary>Queue one piece of the count: the lead-in, or a number.</summary>
        public void Say(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            _arbiter.Emit(text, interrupt: false, SpeechIntent.Queue, VerbosityLevel.Critical,
                "CountdownSpeech.Say", SpeechSubject.CheckCountdown, additive: true);
        }

        /// <summary>True while a piece of the count has been queued and not yet started or finished.</summary>
        public bool Owed => _arbiter.OwesUnstarted(SpeechSubject.CheckCountdown);

        /// <summary>
        /// Take back every piece not yet said: the count was called off, or its
        /// words could not be delivered.
        /// </summary>
        /// <param name="by">Plain prose for the trace, after "superseded by".</param>
        public void Withdraw(string by) =>
            _arbiter.Supersede(SpeechSubject.CheckCountdown, by, "CountdownSpeech.Withdraw");
    }
}
