namespace Radios
{
    /// <summary>
    /// What happened when a caller asked for a write the change-nothing hold
    /// can refuse (#486, 2026-09-02).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a bool was not enough.</b> On 2026-09-01 Noel selected a
    /// microphone profile on his own 8600 with the hold armed. The guard did
    /// exactly its job — it spoke <i>"Change nothing is on for this radio, so
    /// JJ Flexible did not load a profile on it. The setting is in Settings,
    /// under Radios"</i>, naming both the cause and the route. Then the caller
    /// saw <c>false</c>, could not tell a refusal from a breakage, and said
    /// <i>"Could not select profile"</i>. The second sentence won, and he
    /// reported a bug in a build that had obeyed a setting he turned on. A
    /// refusal sends you to Settings; an error sends you to a bug report. The
    /// difference is not cosmetic — it routes the operator to the wrong place
    /// entirely.
    /// </para>
    /// <para>
    /// <b>What a caller does with each value.</b> <see cref="Done"/>: say it
    /// happened. <see cref="Refused"/>: say NOTHING about it — the guard has
    /// already spoken, naming the setting, and a second sentence on top is
    /// the defect this type exists to end. <see cref="Skipped"/>: say why, in
    /// the caller's own terms — the reason is one the caller already knows
    /// (the profile is not on this radio, there is nothing to do). <see
    /// cref="Failed"/>: say it broke.
    /// </para>
    /// <para>
    /// This generalises to every guarded writer that still returns a bool, and
    /// there are many; each hands its caller a boolean that has lost the
    /// distinction between "I declined" and "I broke". They migrate one at a
    /// time, as they are touched, onto this type.
    /// </para>
    /// </remarks>
    public enum GuardedOutcome
    {
        /// <summary>The write went out. Say so.</summary>
        Done = 0,

        /// <summary>The change-nothing hold declined it, and the refusal has
        /// ALREADY been spoken, naming the setting and the route. Stay
        /// silent; a second sentence would overwrite the useful one.</summary>
        Refused = 1,

        /// <summary>Nothing was sent, for a reason the caller already knows
        /// and must say in its own terms — the profile is not on this radio,
        /// there was nothing to select, there is no radio. Not a fault.</summary>
        Skipped = 2,

        /// <summary>Something genuinely broke. Say it broke.</summary>
        Failed = 3,
    }
}
