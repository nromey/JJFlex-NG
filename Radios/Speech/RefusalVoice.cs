#nullable enable
namespace Radios.Speech
{
    /// <summary>
    /// How much a REFUSAL says, by verbosity (#528). A refusal is the answer
    /// to a key that was not accepted: the JJ key layer's unknown key and its
    /// near miss, an arrow pressed before a target is picked, the wrong arrow
    /// pair for the target, a Shift whose side cannot be read. Every one of
    /// them plays the invalid tone; the question here is what is SAID with it.
    ///
    /// <para><b>The rule, ruled by Noel 2026-09-02: scale by verbosity, never
    /// by experience.</b> At Chatty the tone is followed by the full teaching
    /// sentence — which letters pick a target, that H lists the keys, that
    /// Escape cancels. Below Chatty the tone is the whole answer and the
    /// sentence stays unspoken. The operator who has learned what the tone
    /// means turns verbosity down and stops hearing the lesson; the operator
    /// who has not, leaves it up. The content is never deleted, it moves
    /// behind a level the operator already owns and can predict. An adaptive
    /// "say less once you have heard it a few times" was considered and
    /// rejected: an interface that behaves differently from last time, for a
    /// reason the operator cannot see, is worse than a verbose one — and for
    /// someone whose only feedback is sound it is indistinguishable from a
    /// fault.</para>
    ///
    /// <para><b>Off says no more than Terse.</b> A level must never say MORE
    /// than the level above it, or turning verbosity down makes the app
    /// talk more, which is the one thing the control promises not to do. So
    /// Off is tone-only as well. Until this rule the unknown-key sentence was
    /// tagged Critical and spoken at Off while Terse got the same words: the
    /// two lower levels were indistinguishable here.</para>
    ///
    /// <para><b>A refusal is never silent.</b> Earcons can be switched off,
    /// as a whole or by category, and a refused key that produced nothing
    /// at all is the invisible failure this project's no-silent-keystrokes
    /// rule exists to forbid — a key that registered and a key that did not
    /// sound identical. So when the tone cannot sound, the words stand in
    /// for it at every level. That is also why this takes the tone's
    /// audibility as an argument rather than reading it: the engine under
    /// JJFlexWpf has no earcon player and is told by its host.</para>
    ///
    /// <para><b>Verbosity changes what is said, never what happens.</b> The
    /// leader still arms for H, slash and Escape after an unknown key, the
    /// value layer still stays open after a refused arrow, at every level.
    /// An operator who learned at Chatty that the tone means "H lists the
    /// keys" must find that still true at Terse, or the setting about words
    /// has silently changed a behaviour.</para>
    ///
    /// <para>One predicate for both hosts — the value-layer engine and the
    /// leader dispatcher — so the rule cannot drift into two vocabularies,
    /// and so a fourth level, should one arrive, is a change in one place.
    /// The ruling named a middle tier, "a short cue", that the engine has no
    /// level for; the short tiers in the lexicon are what the fallback
    /// speaks, and they are where that tier would be wired.</para>
    /// </summary>
    public static class RefusalVoice
    {
        /// <summary>
        /// True when the tone is the whole answer and the sentence stays
        /// unspoken: below Chatty, and only if the tone will actually sound.
        /// </summary>
        /// <param name="level">The operator's current verbosity.</param>
        /// <param name="toneWillSound">
        /// Whether the invalid tone is wired AND the operator's earcon
        /// switches let it through. False here means the words are the only
        /// feedback there is, so they are spoken at every level.
        /// </param>
        public static bool ToneStandsAlone(VerbosityLevel level, bool toneWillSound)
            => toneWillSound && level < VerbosityLevel.Chatty;
    }
}
