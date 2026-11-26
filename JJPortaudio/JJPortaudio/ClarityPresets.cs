using System;
using System.Collections.Generic;

namespace JJPortaudio
{
    /// <summary>One plain-named JJ Clarity starting point: a stable id and the settings it fills in (Sprint 49 Track J, J2).</summary>
    public sealed class ClarityPreset
    {
        public ClarityPreset(string id, ClaritySettings settings)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>
        /// The stable id: saved, matched and used to find the preset's name. The
        /// name an operator hears is a lexicon entry (DRAFT PROSE, Codex's),
        /// keyed from this id in phase 2; this assembly holds no words.
        /// </summary>
        public string Id { get; }

        public ClaritySettings Settings { get; }
    }

    /// <summary>
    /// JJ Clarity's presets, as data (Sprint 49 Track J, J2). Starting points,
    /// never a mode: loading one fills the levers and every field stays the
    /// operator's to change. Not ruled for Clarity; offered in the shape Noel
    /// ruled for the equaliser (2026-11-23 11:38) and the compressor (20:48).
    /// Each one's reasoning is in Track J's report under "For Noel".
    /// </summary>
    /// <remarks>
    /// <para>The tunes are chosen so that on a 2.9 kHz SSB filter the high band's
    /// brightness lands inside the passband without clamping, except "wide",
    /// which is for ESSB and AM, where the edge is higher; on a narrow filter
    /// its tune clamps and the stage says so (the intent is kept).</para>
    /// <para><b>Drive and mix retuned with the level fix (2026-11-26).</b> Drive
    /// now sets the harmonic's level relative to its band at any microphone
    /// level (<see cref="ClarityExciter.HarmonicRatio"/>), so the first
    /// numbers, chosen before anything measured them, added almost nothing.
    /// Each preset now has a target, the level-fix track's judgement, measured
    /// on a real 90-second headset take of running speech at an everyday level
    /// (speech about -29 dBFS RMS; level no longer matters), through a 100 to
    /// 2,900 Hz passband: gentle lifts 2.0 to 2.9 kHz by about 1 dB, presence
    /// by about 3 dB, fuller's high band by about 0.5 dB; wide lifts 3 to 4 kHz
    /// by about 2 dB on a 50 to 4,000 Hz passband (and on a 2.9 kHz filter, its
    /// tune clamped, lifts 2.0 to 2.9 kHz by about 5 dB). How much a given
    /// setting lifts depends on the voice and on what is being said (a
    /// quieter-sounding stretch of the same take lifted about twice as many
    /// decibels at the same setting), so these are starting points.</para>
    /// <para><b>The low bands were not retuned.</b> On that voice through a
    /// 100 Hz low edge the low band changed 160 to 450 Hz by under 0.1 dB at
    /// these settings and by 0.3 dB (both harmonics) to 0.6 dB (even) at drive
    /// 24 and mix 100: no setting makes it audible there, because the
    /// fundamental already passes the filter and the voice already has its
    /// own harmonics where the band adds them (the prior-art survey's point).
    /// They keep the numbers they had.</para>
    /// </remarks>
    public static class ClarityPresets
    {
        public const string Gentle = "gentle";
        public const string Presence = "presence";
        public const string Fuller = "fuller";
        public const string Wide = "wide";

        private static readonly ClarityPreset[] _all =
        {
            // A gentle brightness for SSB: the second harmonic only, from 2 kHz, about 1 dB.
            new ClarityPreset(Gentle, new ClaritySettings(
                new ClarityBand(true, 2000f, 20f, 50f, ClarityHarmonics.Even),
                new ClarityBand(false, ClaritySettings.LowTuneDefaultHz, ClaritySettings.LowDriveDefaultDb, ClaritySettings.MixDefaultPercent, ClarityHarmonics.Both))),
            // More cut-through for SSB: brightness from 1.8 kHz, driven harder, more of it: about 3 dB.
            new ClarityPreset(Presence, new ClaritySettings(
                new ClarityBand(true, 1800f, 24f, 60f, ClarityHarmonics.Even),
                new ClarityBand(false, ClaritySettings.LowTuneDefaultHz, ClaritySettings.LowDriveDefaultDb, ClaritySettings.MixDefaultPercent, ClarityHarmonics.Both))),
            // A fuller sound for a thin microphone: a little brightness (about 0.5 dB), and the low band enriching fundamentals up to 150 Hz.
            new ClarityPreset(Fuller, new ClaritySettings(
                new ClarityBand(true, 2000f, 18f, 40f, ClarityHarmonics.Even),
                new ClarityBand(true, 150f, 9f, 30f, ClarityHarmonics.Both))),
            // Wider, for ESSB or AM: brightness from 3 kHz with the third harmonic (about 2 dB in 3 to 4 kHz), and some fullness.
            new ClarityPreset(Wide, new ClaritySettings(
                new ClarityBand(true, 3000f, 24f, 63f, ClarityHarmonics.Odd),
                new ClarityBand(true, 120f, 6f, 20f, ClarityHarmonics.Even))),
        };

        /// <summary>Every preset, in the order a list offers them.</summary>
        public static IReadOnlyList<ClarityPreset> All => _all;

        /// <summary>The preset with this id, or null.</summary>
        public static ClarityPreset Find(string id)
        {
            foreach (var p in _all) if (string.Equals(p.Id, id, StringComparison.Ordinal)) return p;
            return null;
        }
    }
}
