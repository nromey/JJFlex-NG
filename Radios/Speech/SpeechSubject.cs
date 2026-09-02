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
        /// What this application decided to do about the PROFILES on the radio
        /// just connected to — applied the operator's transmit audio, loaded
        /// their whole set, left everything alone, or could not and why.
        ///
        /// <para><b>Exactly one of these is true at a time</b>, which is what
        /// makes them one subject: a later verdict does not merely follow the
        /// earlier one, it REPLACES it. "Your transmit audio is applied" and
        /// "this radio's profiles were left alone" cannot both describe the
        /// same connection, so an unheard one is worthless the moment the
        /// other exists.</para>
        ///
        /// <para><b>Keyed after the Sprint 44 integration pass, not by the
        /// track that wrote them.</b> Track D authored seven of these against
        /// the API as it stood before Track A landed, so they were unkeyed —
        /// and one of them, "This radio's profiles were left alone…", is a
        /// message the 2026-09-01 traces show being DROPPED three times. The
        /// sprint that fixed the channel would otherwise have shipped seven
        /// new emitters that did not use the fix, which is exactly the
        /// confound it existed to remove: an announcement that goes missing
        /// during a guest-radio test must not leave the operator unable to
        /// tell a broken feature from a dropped sentence.</para>
        ///
        /// <para>These fire at connect, in a burst, across window changes —
        /// the worst case the arbiter has.</para>
        /// </summary>
        public const string ProfileGuestOutcome = "profile-guest-outcome";

        /// <summary>
        /// The radio the operator is on, as stated by the connect briefing's
        /// lead — "Connected to FLEX-8600, SmartLink, 4 slices." One
        /// connection at a time, so the next connect's lead replaces an
        /// unheard one. Emitted by <see cref="ConnectBriefing"/> at the
        /// settle moment, never by a call site of its own (#510).
        /// </summary>
        public const string ConnectLead = "connect-lead";

        /// <summary>
        /// Where the operator is — the Home arrival ("JJ Flexible Home,
        /// Modern tuning mode") and the Home landing prefix ("JJ Flexible
        /// Home, slice, 14.100.000"). Only the newest is true: an arrival
        /// still unheard when a landing prefix speaks is covered by it.
        /// Dialog titles are deliberately NOT here — see the #503 notes on
        /// why "where focus is" across every window is a design of its own.
        /// </summary>
        public const string WhereYouAre = "where-you-are";

        /// <summary>
        /// The radio's own mic-profile selection at connect — repaired by
        /// loading one, or found empty and warned about. Two verdicts on one
        /// radio cannot both be true, so the newer replaces the older.
        /// </summary>
        public const string MicProfileOnRadio = "mic-profile-on-radio";

        /// <summary>
        /// Instrumentation running that the operator cannot see — "Recording
        /// is on." (#194 by way of #253). Superseded by the next notice about
        /// what is running.
        /// </summary>
        public const string RunningInstrumentation = "running-instrumentation";

        /// What the JJ key offers from here — the command list behind
        /// <c>JJ key H</c>, the explorer behind <c>JJ key slash</c>, and the
        /// layer's own answers to a key it did not know (the near miss, the
        /// unknown-key sentence). One subject because they answer one
        /// question, "what can I press?", and only the newest answer is worth
        /// hearing: an operator who presses H twice wants the list from the
        /// top, not the tail of the first reading and then the second, and an
        /// unknown-key sentence still queued when the list starts has done
        /// its job. Nothing else supersedes it — a toggle, a slice jump or a
        /// tune leaves the map exactly as true as it was.
        /// </summary>
        public const string JjKeyHelp = "jj-key-help";

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

        /// <summary>
        /// One target inside a value sub-layer (<see cref="ValueSubLayer"/>),
        /// named by the layer and the target — the audio layer's pan, the
        /// filter layer's low edge. Every nudge, every selection announcement
        /// and every spoken answer about that target share it, so a held
        /// arrow settles to the tail value and the answer to "S" is covered
        /// by the next move. This is also the target's Latest coalesce key,
        /// for the same reason <see cref="ValueField"/> is: they were always
        /// one identity. A single-value layer passes an empty target and
        /// gets the layer's own name.
        /// </summary>
        public static string ValueLayer(string layerId, string targetId = "")
            => string.IsNullOrEmpty(targetId)
                ? "value-layer:" + layerId
                : "value-layer:" + layerId + ":" + targetId;

        /// <summary>
        /// The state of a value sub-layer as a whole — entered, which group
        /// is active, the in-layer help, closed, restored. One of these is
        /// true at a time: "Audio layer closed" makes an unheard entry
        /// sentence worthless, and the help re-states everything the entry
        /// said. Kept apart from <see cref="ValueLayer"/> so closing the layer
        /// cannot retire a value announcement that is still true.
        /// </summary>
        public static string ValueLayerStatus(string layerId) => "value-layer-status:" + layerId;
    }
}
