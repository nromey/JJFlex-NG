namespace Radios.Speech
{
    /// <summary>
    /// What a <see cref="SpeechIntent.Latest"/> key IS — the difference between
    /// a value being swept and a question being asked.
    ///
    /// <para><b>The rule, agreed 2026-08-27 (#264): a key that asks a question
    /// is not a value that sweeps.</b> They want opposite things from the
    /// coalescer. A sweep wants the TAIL — hold the arrow, and the only reading
    /// worth hearing is where you stopped. A re-request wants an ANSWER NOW —
    /// press Ctrl+S again and you are asking the same question a second time,
    /// not moving anything.</para>
    ///
    /// <para><b>Why the distinction had to become a type.</b> Nothing
    /// distinguished them, so the sweep rule was applied to both, and a second
    /// Ctrl+S within <see cref="SpeechArbiter.SweepWindowMs"/> was classified as
    /// sweeping a value and given the settle treatment: wait for the sweep to
    /// stop, then speak. Measured at the radio on 2026-08-27, the residual was
    /// about half a second on a key whose entire job is to answer immediately.
    /// It is a CLASSIFICATION change and deliberately not a constant change —
    /// shortening the sweep window would degrade the sweeps that constant
    /// exists for.</para>
    ///
    /// <para><b>This replaces the <c>repeatWhileHeld</c> flag rather than
    /// joining it.</b> That flag meant "an identical repeat is information, not
    /// noise", which is true of a query and of nothing else; it had exactly one
    /// caller in the application, and that caller was the query key. Keeping
    /// both would have left two names for one idea at one call site, which is
    /// the duplication this codebase keeps paying to remove. Everything the
    /// flag did, <see cref="Query"/> now does — and it also carries the part
    /// the flag could not express, which is that the press was never a sweep in
    /// the first place.</para>
    /// </summary>
    public enum SpeechCoalesceKind
    {
        /// <summary>
        /// A value the operator is moving: gain, volume, slice volume, a value
        /// field, the VFO. Lead, then settle — repeated presses coalesce and
        /// the final value speaks once the sweep stops, and an identical repeat
        /// is dropped because saying it again tells the operator nothing.
        ///
        /// The default, because it is what almost every Latest key is, and
        /// because the settle policy is correct here: it is what stops a held
        /// key from being heard as clicks and ticks.
        /// </summary>
        Value = 0,

        /// <summary>
        /// A key that asks a question and expects an answer: the S-meter on
        /// Ctrl+S. Nothing is in flight, so there is no sweep to wait out.
        ///
        /// Three consequences, all of them the same rule seen from different
        /// sides. A re-press is never classified as sweeping, so it answers
        /// straight away rather than waiting out a settle. A newer press never
        /// defers the pending answer — the operator asking again must not push
        /// their own answer further away. And an identical reading is spoken
        /// rather than swallowed, because "still S 7" is precisely how you
        /// learn a signal is steady.
        ///
        /// One thing a query does NOT escape: the anti-clip gap. That is not a
        /// settle and not a policy about sweeps — it is the physical floor that
        /// keeps two readings from cutting each other into clicks, and it is
        /// what turns a HELD query key into a readable cadence instead of a
        /// stutter. Removing it here would rebuild the "r r r r r RF gain 5"
        /// defect of 2026-08-18 on the one key most likely to be hammered.
        /// </summary>
        Query,
    }
}
