#nullable enable
namespace Radios.Speech
{
    /// <summary>
    /// What an utterance is ABOUT — the identity the arbiter uses to decide
    /// whether a newer announcement has made an unheard older one worthless.
    ///
    /// <para><b>Why a subject and not a timer.</b> Until #503 the ledger
    /// expired a rescued utterance by a bound derived from its own word count:
    /// twice the estimated time to say it. That made lifetime a function of
    /// LENGTH, and length is inversely related to how long a message stays
    /// true. <c>SWR 1.7</c> — seven characters, the answer to a question the
    /// operator asked by keying up — got 1.6 seconds; a 300-character courtesy
    /// paragraph about mic profiles got thirty. Measured across 2026-09-01: 89
    /// drops, 52 of them never re-spoken even once, and the one keypress that
    /// discarded the SWR reading was the operator pressing Tune AGAIN because
    /// they had heard nothing — the retry a silent system invites was the
    /// event that guaranteed the silence.</para>
    ///
    /// <para><b>The discriminator is supersession, not age.</b> Some of those
    /// 89 were dropped correctly: the digits <c>1</c> and <c>5</c> typed into a
    /// field are worthless once <c>Tune Power 15</c> has been said, and a
    /// progress line is worthless the moment the next one exists. What makes
    /// them worthless is not that they are old — it is that something newer
    /// covers the same subject. <c>SWR 1.7</c> is covered by nothing: no later
    /// announcement says what the tune measured, so it stays worth hearing
    /// until the next tune says otherwise.</para>
    ///
    /// <para><b>The emitter declares the subject; the arbiter never infers
    /// it.</b> Pattern-matching message text to guess what it is about is the
    /// description-drift defect this project loses days to — the words change
    /// and the guess silently stops matching. The call site is the only place
    /// that knows what the announcement is about, so the call site says so.
    /// Utterances sharing a subject replace one another: before emission for
    /// <see cref="SpeechIntent.Latest"/> (coalescing, the existing
    /// <c>coalesceKey</c>), and in the salvage ledger for everything else
    /// (supersession). One idea, two stages.</para>
    ///
    /// <para><b>An unkeyed utterance keeps the word-count bound.</b> Most
    /// emitters declare nothing today, and for those the arbiter cannot know
    /// what would supersede them — so it keeps the conservative bound rather
    /// than keeping everything, because a stale "Muted" re-spoken after an
    /// unheard "Unmuted" is a lie, and the old bound at least kills those
    /// quickly. Declaring a subject is the emitter saying "this remains true
    /// until something with the same subject replaces it", and that is a
    /// statement only the emitter can make. The drop trace says "no subject
    /// declared" so each remaining stale drop names its own fix.</para>
    ///
    /// <para><b>The constants here are the vocabulary.</b> A subject is a
    /// plain string so that parameterised identities (a value field by its
    /// label, a slice by its letter) can be expressed, but every spelling
    /// lives in this one class so two emitters cannot invent two names for
    /// one thing. Add a constant here rather than a literal at a call site.</para>
    /// </summary>
    public static class SpeechSubject
    {
        /// <summary>
        /// The narration of a slow operation — "Looking for radios", "Still
        /// looking", "Connected to X. Waiting for slice...". Only the newest
        /// line is ever worth hearing, and none of them once the operation has
        /// ended, which <see cref="ProgressVoice"/> declares through
        /// <see cref="ScreenReaderOutput.Supersede"/> when it stops.
        /// </summary>
        public const string Progress = "progress";

        /// <summary>
        /// Whether radio audio is playing through this computer. "PC audio
        /// on." is true until "PC audio off" or "could not start" replaces it,
        /// and nothing else replaces it — a tune, a band change or a focus
        /// move leaves it exactly as true as it was.
        /// </summary>
        public const string PcAudio = "pc-audio";

        /// <summary>
        /// The SWR measured by the tune that just ended. The answer to a
        /// question the operator asked by keying up; covered only by the next
        /// tune's reading. Pressing Tune again is NOT what covers it — that
        /// press is the retry a lost answer provokes, and it must deliver the
        /// answer rather than destroy it (#503, the 2026-09-01 case).
        /// </summary>
        public const string SwrAfterTune = "swr-after-tune";

        /// <summary>
        /// The receipt that a change will not survive disconnect unless the
        /// profile is saved (#442). One reminder outstanding at a time: the
        /// newest change's receipt covers every earlier one.
        /// </summary>
        public const string ProvisionalReceipt = "provisional-receipt";

        /// <summary>
        /// The value of one field, named by its label — the committed value
        /// and the swept value share it, so a committed value still queued
        /// when the operator starts sweeping is covered by the sweep. This is
        /// also the field's Latest coalesce key, on purpose: they were always
        /// the same identity.
        /// </summary>
        public static string ValueField(string label) => "value-field:" + label;

        /// <summary>
        /// The keystrokes of a value being typed into one field — the digit
        /// echoes, the point, the sign, a delete. Emitted ADDITIVE, because a
        /// digit must not supersede the digit before it: interrupted
        /// mid-entry, the operator needs "1, 5" again, not a lone "5" over a
        /// field that reads 15. Deliberately not <see cref="ValueField"/>
        /// either, so retiring the entry cannot retire a committed value that
        /// is still true. The echoes are retired together, by the field
        /// calling <see cref="ScreenReaderOutput.Supersede"/> when the entry
        /// ends — in a value, a rejection or a cancel — which is what makes
        /// "1" and "5" worthless once "Tune Power 15" is said.
        /// </summary>
        public static string ValueEntry(string label) => "value-entry:" + label;
    }
}
