#nullable enable
namespace Radios
{
    /// <summary>
    /// Why the TX band lockout refused a transmission (#1310), or
    /// <see cref="None"/> when it did not.
    /// </summary>
    public enum LockoutReason
    {
        /// <summary>The lockout admits it, or is off.</summary>
        None,
        /// <summary>Some of the signal is outside every band division the licence class has.</summary>
        Frequency,
        /// <summary>The signal is inside the class's privileges, but the mode is not allowed there
        /// (97.305(c), 97.307(f)(9) and (10)).</summary>
        Mode,
    }

    /// <summary>
    /// The one judgement of a transmission against the rules JJ Flexible adds on
    /// top of the radio's own (ruled by Noel 2026-11-19 for #1161: the check
    /// returns a reason, not yes or no, so one judge serves every rule and the
    /// refusal can say what was wrong and which setting refused it).
    /// </summary>
    /// <remarks>
    /// <para>The default value is <see cref="Allowed"/>: a judge that was never
    /// wired, or could not read the radio, admits, as the lockout always has.</para>
    /// <para>Each door speaks <see cref="RefusalSentence"/>. The approved
    /// <c>audio.ptt.blocked_by_license</c> is said for the lockout's frequency
    /// reason only, word for word; every other reason has its own sentence, which
    /// names the setting that refused it.</para>
    /// </remarks>
    public readonly record struct TransmitVerdict(LockoutReason Lockout)
    {
        /// <summary>Nothing refused.</summary>
        public static TransmitVerdict Allowed => default;

        /// <summary>The lockout's frequency refusal: what a bare "no" from an older
        /// yes-or-no check means, so its approved sentence is kept.</summary>
        public static TransmitVerdict LockoutFrequency => new(LockoutReason.Frequency);

        /// <summary>True when nothing refused the transmission.</summary>
        public bool IsAllowed => Lockout == LockoutReason.None;

        /// <summary>
        /// The sentence a door speaks for this refusal; null when nothing was
        /// refused. Each key is a full literal (LexiconKeyCoverageTests).
        /// </summary>
        public string? RefusalSentence() => Lockout switch
        {
            LockoutReason.Frequency => Lexicon.Get("audio.ptt.blocked_by_license"),
            LockoutReason.Mode => Lexicon.Get("audio.ptt.blocked_by_license_mode"),
            _ => null,
        };
    }
}
