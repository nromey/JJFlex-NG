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
    /// The tunes are chosen so that on a 2.9 kHz SSB filter the high band's
    /// brightness lands inside the passband without clamping, except "wide",
    /// which is for ESSB and AM, where the edge is higher; on a narrow filter
    /// its tune clamps and the stage says so (the intent is kept).
    /// </remarks>
    public static class ClarityPresets
    {
        public const string Gentle = "gentle";
        public const string Presence = "presence";
        public const string Fuller = "fuller";
        public const string Wide = "wide";

        private static readonly ClarityPreset[] _all =
        {
            // A gentle brightness for SSB: the second harmonic only, from 2 kHz, a light mix.
            new ClarityPreset(Gentle, new ClaritySettings(
                new ClarityBand(true, 2000f, 6f, 20f, ClarityHarmonics.Even),
                new ClarityBand(false, ClaritySettings.LowTuneDefaultHz, ClaritySettings.LowDriveDefaultDb, ClaritySettings.MixDefaultPercent, ClarityHarmonics.Both))),
            // More cut-through for SSB: brightness from 1.8 kHz, driven harder, more of it.
            new ClarityPreset(Presence, new ClaritySettings(
                new ClarityBand(true, 1800f, 15f, 35f, ClarityHarmonics.Even),
                new ClarityBand(false, ClaritySettings.LowTuneDefaultHz, ClaritySettings.LowDriveDefaultDb, ClaritySettings.MixDefaultPercent, ClarityHarmonics.Both))),
            // A fuller sound for a thin microphone: a little brightness, and the low band enriching fundamentals up to 150 Hz.
            new ClarityPreset(Fuller, new ClaritySettings(
                new ClarityBand(true, 2000f, 6f, 15f, ClarityHarmonics.Even),
                new ClarityBand(true, 150f, 9f, 30f, ClarityHarmonics.Both))),
            // Wider, for ESSB or AM: brightness from 3 kHz with the third harmonic, and some fullness.
            new ClarityPreset(Wide, new ClaritySettings(
                new ClarityBand(true, 3000f, 9f, 30f, ClarityHarmonics.Odd),
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
