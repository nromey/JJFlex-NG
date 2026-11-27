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
    /// Why the 60 m rules ("Enforce transmit rules" on Settings, License) refused
    /// a transmission (#1161), or <see cref="None"/> when they did not. Judged only
    /// for a signal on the 60 m band (<see cref="SixtyMeterChannels.JudgeTransmission"/>).
    /// </summary>
    public enum SixtyMeterReason
    {
        /// <summary>Lawful, not on 60 m, or the rules are off.</summary>
        None,
        /// <summary>On a channel in a kept mode, but not on the exact dial for it: CW on the
        /// centre, USB and Digital USB 1.5 kHz below it (97.303(h)(3)).</summary>
        Dial,
        /// <summary>On a channel in a mode JJ Flexible does not allow there: it allows upper
        /// sideband, Digital USB, RTTY and CW, the designators 97.303(h)(3) names for the
        /// channels. Whether that list is exhaustive is not established, so this is the
        /// conservative reading (the orchestrator's decision, repair round 1 of junk box
        /// batch 31; night question J8), never a claim about the law.</summary>
        ChannelMode,
        /// <summary>A mode far wider than the 2.8 kHz 60 m allows (the FM modes, D-STAR),
        /// whatever its filter (97.307(f)(14)).</summary>
        TooWideMode,
        /// <summary>The radio's reported transmit filter makes the signal wider than 2.8 kHz,
        /// or carries part of it past the channel's or the segment's edge.</summary>
        Emission,
        /// <summary>A CW signal, judged as 150 Hz centred on its carrier (nothing measured),
        /// would reach past the channel's or the segment's edge (repair round 1).</summary>
        EmissionCwEstimated,
        /// <summary>The radio has not reported its transmit filter, so the app's default
        /// (100 to 2,900 Hz) was assumed, and at that width the signal would be too wide or
        /// past an edge (repair round 1; the assumption is night question J4's).</summary>
        EmissionFilterAssumed,
        /// <summary>On the 60 m band, but on no channel and outside the segment.</summary>
        Outside,
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
    public readonly record struct TransmitVerdict(LockoutReason Lockout, SixtyMeterReason SixtyMeter = SixtyMeterReason.None)
    {
        /// <summary>Nothing refused.</summary>
        public static TransmitVerdict Allowed => default;

        /// <summary>The lockout's frequency refusal: what a bare "no" from an older
        /// yes-or-no check means, so its approved sentence is kept.</summary>
        public static TransmitVerdict LockoutFrequency => new(LockoutReason.Frequency);

        /// <summary>True when nothing refused the transmission.</summary>
        public bool IsAllowed => Lockout == LockoutReason.None && SixtyMeter == SixtyMeterReason.None;

        /// <summary>
        /// The sentence a door speaks for this refusal; null when nothing was
        /// refused. When both the lockout and the 60 m rules refused, both are
        /// named, because turning one off would not let the key through. Each key
        /// is a full literal (LexiconKeyCoverageTests).
        /// </summary>
        public string? RefusalSentence()
        {
            string? lockout = Lockout switch
            {
                LockoutReason.Frequency => Lexicon.Get("audio.ptt.blocked_by_license"),
                LockoutReason.Mode => Lexicon.Get("audio.ptt.blocked_by_license_mode"),
                _ => null,
            };
            string? sixty = SixtyMeter switch
            {
                SixtyMeterReason.Dial => Lexicon.Get("audio.ptt.blocked_sixty_meter_dial"),
                SixtyMeterReason.ChannelMode => Lexicon.Get("audio.ptt.blocked_sixty_meter_channel_mode"),
                SixtyMeterReason.TooWideMode => Lexicon.Get("audio.ptt.blocked_sixty_meter_too_wide_mode"),
                SixtyMeterReason.Emission => Lexicon.Get("audio.ptt.blocked_sixty_meter_emission"),
                SixtyMeterReason.EmissionCwEstimated => Lexicon.Get("audio.ptt.blocked_sixty_meter_cw_edge"),
                SixtyMeterReason.EmissionFilterAssumed => Lexicon.Get("audio.ptt.blocked_sixty_meter_filter_assumed"),
                SixtyMeterReason.Outside => Lexicon.Get("audio.ptt.blocked_sixty_meter_outside"),
                _ => null,
            };
            if (lockout != null && sixty != null)
                return Lexicon.Get("audio.ptt.blocked_by_both", ("lockout", lockout), ("sixty", sixty));
            return lockout ?? sixty;
        }
    }
}
