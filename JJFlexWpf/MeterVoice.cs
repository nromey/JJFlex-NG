using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Serialization;

namespace JJFlexWpf
{
    /// <summary>
    /// A meter voice: a named, serialisable parameter set describing a timbre —
    /// partial amplitudes, modulation, attack character, gating pattern and
    /// noise content. Voices are first-class data, deliberately NOT an enum:
    /// they can be authored, saved, shared as packs, referenced by meters, and
    /// later reused by waterfall categories. Meters reference a voice by name
    /// (see <see cref="MeterDefinition.VoiceName"/>); the synthesis engine
    /// (<see cref="VoicedToneSampleProvider"/>) renders whatever the parameters
    /// say, with no per-voice code anywhere.
    ///
    /// The governing sonification grammar (kerchunk-sidetone-pileup.md):
    /// TIMBRE identifies the meter, PITCH carries its value, PAN enhances but
    /// is never load-bearing. So nothing in this type encodes pitch or pan —
    /// those belong to the meter that references the voice. Everything here is
    /// identity: what the tone is made of and what it does.
    ///
    /// Thread-safety contract with the synthesis engine: scalar properties may
    /// be adjusted live while the voice is playing (float writes are atomic and
    /// the renderer re-reads them every buffer). The <see cref="Partials"/>
    /// array must be REPLACED wholesale, never mutated element-by-element, so
    /// the renderer always sees a consistent spectrum.
    /// </summary>
    public class MeterVoice
    {
        /// <summary>
        /// The voice's identity. Meters and (later) waterfall categories
        /// reference a voice by this name. Built-in names are reserved; user
        /// voices may not shadow them (see <see cref="MeterVoiceLibrary"/>).
        /// </summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// Operator-facing description for pickers and spoken rows, e.g.
        /// "Bell — struck, ringing, repeats". Plain words, no synthesis jargon.
        /// </summary>
        public string Description { get; set; } = "";

        /// <summary>
        /// True for voices shipped in code (<see cref="MeterVoiceLibrary"/>).
        /// Built-ins are never persisted and never edited in place — a live
        /// tweak of a built-in becomes a per-meter override or a new named
        /// voice. This keeps a shared vocabulary shared.
        /// </summary>
        [XmlIgnore]
        public bool BuiltIn { get; set; }

        // ------------------------------------------------------------------
        // Timbre — what the tone is made of
        // ------------------------------------------------------------------

        /// <summary>
        /// Additive-synthesis partial amplitudes. Index 0 is the fundamental,
        /// index n is harmonic n+1. Amplitudes are relative; the renderer
        /// normalises equal-power so voices with different partial counts play
        /// at comparable loudness. Partials above Nyquist are skipped at
        /// render time. Replace wholesale at runtime, never mutate in place.
        /// </summary>
        public float[] Partials { get; set; } = { 1f };

        /// <summary>
        /// Spectral tilt, -1..+1. 0 leaves <see cref="Partials"/> as stored;
        /// positive emphasises upper partials (cuts through better), negative
        /// mellows. This is the single live "brightness" knob the tone-tweak
        /// layer adjusts, so a tweak never has to regenerate the partial set.
        /// </summary>
        public float Brightness { get; set; }

        /// <summary>
        /// Partial stretch, 0 = perfectly harmonic. Positive values detune
        /// upper partials sharp the way a struck bell's are: partial n plays
        /// at f·(n+1)·(1 + Inharmonicity·n). Small values (0.02–0.06) read as
        /// "metallic"; large values dissolve into clangour.
        /// </summary>
        public float Inharmonicity { get; set; }

        // ------------------------------------------------------------------
        // Modulation — what the tone does (second identity axis, perceptually
        // orthogonal to pitch: a 6 Hz tremolo is the same tremolo on any note)
        // ------------------------------------------------------------------

        /// <summary>Amplitude-modulation rate in Hz. 0 = none. ~5 Hz reads as
        /// throb, ~28 Hz as a rolled-R trill, 40–70 Hz as roughness/rasp.</summary>
        public float TremoloRateHz { get; set; }

        /// <summary>Amplitude-modulation depth 0..1.</summary>
        public float TremoloDepth { get; set; }

        /// <summary>Frequency-modulation rate in Hz. 0 = none.</summary>
        public float VibratoRateHz { get; set; }

        /// <summary>Frequency-modulation depth in semitones.</summary>
        public float VibratoDepthSemitones { get; set; }

        // ------------------------------------------------------------------
        // Pattern — gating and pitch alternation
        // ------------------------------------------------------------------

        /// <summary>Gate on-time in ms. 0 = continuous (no gating). With the
        /// envelope, gating gives repeating strikes (bell) or swells.</summary>
        public float GateOnMs { get; set; }

        /// <summary>Gate off-time in ms. Only meaningful when GateOnMs &gt; 0.</summary>
        public float GateOffMs { get; set; }

        /// <summary>
        /// Pitch alternation interval in semitones, 0 = none. When set, the
        /// tone alternates between its base pitch and base + interval at
        /// <see cref="AlternateRateHz"/> — phone-ring warble at ~18 Hz, Noel's
        /// 500 ms two-tone at 1 Hz. The base pitch still carries the value.
        /// </summary>
        public float AlternateIntervalSemitones { get; set; }

        /// <summary>Full alternation cycles per second (each cycle spends half
        /// its time on each pitch). 1 Hz = 500 ms per pitch.</summary>
        public float AlternateRateHz { get; set; }

        // ------------------------------------------------------------------
        // Attack character — pluck versus swell
        // ------------------------------------------------------------------

        /// <summary>Attack time in ms. Small (1–5) reads as a pluck or strike,
        /// large (200+) as a swell. Retriggers on every gate-on edge when the
        /// voice is gated; otherwise applies once at activation.</summary>
        public float AttackMs { get; set; } = 5f;

        /// <summary>Decay time in ms after the attack peak, falling toward
        /// <see cref="SustainLevel"/>. 0 = no decay (organ-style sustain).</summary>
        public float DecayMs { get; set; }

        /// <summary>Level sustained after decay, 0..1 of peak. A bell decays
        /// toward a low sustain; a pad sustains at 1.</summary>
        public float SustainLevel { get; set; } = 1f;

        // ------------------------------------------------------------------
        // Noise — breath and rasp
        // ------------------------------------------------------------------

        /// <summary>Filtered-noise mix level 0..1 added to the partial sum.
        /// 0 = pure tone. High values with low partial amplitudes give a
        /// breathy, band-of-noise voice that still carries pitch.</summary>
        public float NoiseLevel { get; set; }

        /// <summary>Bandwidth of the noise bandpass in Hz. Narrow (100–300)
        /// is whistly breath; wide (1000+) is hiss.</summary>
        public float NoiseBandwidthHz { get; set; } = 300f;

        /// <summary>
        /// When true (the default), the noise band is centred on the current
        /// tone pitch so a noise voice still carries the meter's value — the
        /// grammar demands pitch carry value even for noise timbres. When
        /// false, the band sits at <see cref="NoiseCenterHz"/> regardless.
        /// </summary>
        public bool NoiseTracksPitch { get; set; } = true;

        /// <summary>Fixed noise-band centre in Hz, used only when
        /// <see cref="NoiseTracksPitch"/> is false.</summary>
        public float NoiseCenterHz { get; set; } = 800f;

        /// <summary>Deep copy — the basis of the per-meter override workflow:
        /// clone, tweak the clone live, then keep-as-copy / replace / discard.</summary>
        public MeterVoice Clone()
        {
            var c = (MeterVoice)MemberwiseClone();
            c.Partials = (float[])Partials.Clone();
            return c;
        }
    }

    /// <summary>
    /// The voice vocabulary: built-in voices (shipped as data in code, so they
    /// can improve between versions without config migration) plus user-authored
    /// voices (persisted in <see cref="AudioOutputConfig.UserVoices"/>).
    /// Lookup is by name. User voices may not shadow built-in names — a tweak
    /// of "Bell" saves as a NEW name, never silently rewrites the shared
    /// meaning of "Bell" for every feature that references it.
    /// </summary>
    public static class MeterVoiceLibrary
    {
        /// <summary>The voice every meter falls back to when its reference
        /// cannot be resolved. Always present.</summary>
        public const string DefaultVoiceName = "Pure";

        private static readonly object _lock = new();
        private static List<MeterVoice> _userVoices = new();

        /// <summary>
        /// Built-in voices, constructed as data. The set is an ALPHABET, not a
        /// continuum: identities differ on at least two of the perceptual axes
        /// (spectrum, modulation rate, attack/pattern), because listeners
        /// reliably separate only five to seven values per single axis.
        /// Modulation-rate ladder across the set: 0 / ~5 / ~18 / 28 / 65 Hz,
        /// plus slow gating — every rung at least 50% above the last, well past
        /// the just-noticeable difference.
        /// </summary>
        public static IReadOnlyList<MeterVoice> BuiltIns { get; } = new List<MeterVoice>
        {
            new MeterVoice
            {
                Name = "Pure", BuiltIn = true,
                Description = "Smooth steady tone",
                Partials = new[] { 1f },
                AttackMs = 8f,
            },
            new MeterVoice
            {
                Name = "Hollow", BuiltIn = true,
                Description = "Woody, hollow, gentle slow wobble",
                // Odd harmonics with steep rolloff — clarinet-ish.
                Partials = new[] { 1f, 0f, 0.45f, 0f, 0.28f, 0f, 0.18f, 0f, 0.12f },
                VibratoRateHz = 5f, VibratoDepthSemitones = 0.3f,
                AttackMs = 12f,
            },
            new MeterVoice
            {
                Name = "Reedy", BuiltIn = true,
                Description = "Bright and nasal, like a reed instrument",
                // Energy concentrated in harmonics 2-4 — oboe-ish.
                Partials = new[] { 0.6f, 1f, 0.85f, 0.7f, 0.5f, 0.35f, 0.25f, 0.15f },
                AttackMs = 10f,
            },
            new MeterVoice
            {
                Name = "Organ", BuiltIn = true,
                Description = "Full and churchy, slow shimmer",
                // Octave-heavy drawbar stack: harmonics 1, 2, 4, 8.
                Partials = new[] { 1f, 0.85f, 0f, 0.7f, 0f, 0f, 0f, 0.5f },
                TremoloRateHz = 5.5f, TremoloDepth = 0.25f,
                AttackMs = 20f,
            },
            new MeterVoice
            {
                Name = "Bell", BuiltIn = true,
                Description = "Struck bell, rings and repeats",
                Partials = new[] { 1f, 0.7f, 0f, 0.85f, 0f, 0.45f, 0f, 0.3f },
                Inharmonicity = 0.04f,
                AttackMs = 2f, DecayMs = 350f, SustainLevel = 0.25f,
                GateOnMs = 450f, GateOffMs = 250f,
            },
            new MeterVoice
            {
                Name = "Trill", BuiltIn = true,
                Description = "Fast flutter, like a rolled R",
                Partials = new[] { 1f, 0.4f, 0.2f },
                TremoloRateHz = 28f, TremoloDepth = 0.85f,
                AttackMs = 8f,
            },
            new MeterVoice
            {
                Name = "Raspy", BuiltIn = true,
                Description = "Rough and buzzy, a touch of grit",
                Partials = new[] { 1f, 0.5f, 0.6f, 0.35f, 0.4f },
                // 65 Hz amplitude modulation sits squarely in the psychoacoustic
                // roughness band — reads as texture, never as pulsing.
                TremoloRateHz = 65f, TremoloDepth = 0.7f,
                NoiseLevel = 0.15f, NoiseBandwidthHz = 400f,
                AttackMs = 8f,
            },
            new MeterVoice
            {
                Name = "Thin", BuiltIn = true,
                Description = "Thin and pinched, slow waver, cuts through",
                // 10% duty-cycle pulse spectrum — one oscillator's worth of nasal.
                // The gentle vibrato exists because the separation screen
                // (tools/voicelab) flagged Thin and Reedy as near-twins: both
                // bright, both static. Pitch motion gives Thin an identity
                // axis Reedy lacks.
                Partials = PartialsFromPulseWidth(0.10f, 12),
                VibratoRateHz = 4.5f, VibratoDepthSemitones = 0.4f,
                AttackMs = 6f,
            },
            new MeterVoice
            {
                Name = "Square", BuiltIn = true,
                Description = "Classic buzzy square wave",
                Partials = PartialsFromSquare(9),
                AttackMs = 6f,
            },
            new MeterVoice
            {
                Name = "Breath", BuiltIn = true,
                Description = "Airy whistle of noise",
                Partials = new[] { 0.35f },
                NoiseLevel = 0.9f, NoiseBandwidthHz = 250f, NoiseTracksPitch = true,
                AttackMs = 25f,
            },
            new MeterVoice
            {
                Name = "Ring", BuiltIn = true,
                Description = "Telephone-style fast warble",
                Partials = new[] { 1f, 0.3f },
                AlternateIntervalSemitones = 3f, AlternateRateHz = 18f,
                AttackMs = 8f,
            },
            new MeterVoice
            {
                Name = "Two-Tone", BuiltIn = true,
                Description = "Alternates high and low every half second",
                Partials = new[] { 1f, 0.3f },
                AlternateIntervalSemitones = 5f, AlternateRateHz = 1f,
                AttackMs = 10f,
            },
            new MeterVoice
            {
                Name = "Swell", BuiltIn = true,
                Description = "Soft repeating swells",
                Partials = new[] { 1f, 0.5f, 0.33f, 0.25f },
                AttackMs = 300f, DecayMs = 0f, SustainLevel = 1f,
                GateOnMs = 700f, GateOffMs = 300f,
            },
            new MeterVoice
            {
                Name = "Pulsing", BuiltIn = true,
                Description = "Gentle on-off pulsing",
                Partials = new[] { 1f },
                GateOnMs = 300f, GateOffMs = 300f,
                AttackMs = 10f,
            },
            new MeterVoice
            {
                Name = "Urgent", BuiltIn = true,
                Description = "Rapid urgent pulsing",
                Partials = new[] { 1f, 0.4f },
                GateOnMs = 50f, GateOffMs = 50f,
                AttackMs = 2f,
            },
        };

        /// <summary>All voice names, built-ins first, then user voices.</summary>
        public static IReadOnlyList<string> AllNames
        {
            get
            {
                lock (_lock)
                {
                    return BuiltIns.Select(v => v.Name)
                        .Concat(_userVoices.Select(v => v.Name))
                        .ToList();
                }
            }
        }

        /// <summary>
        /// Resolve a voice by name. Built-ins win, then user voices; null when
        /// nothing matches (callers fall back to <see cref="DefaultVoiceName"/>).
        /// </summary>
        public static MeterVoice? Find(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var b = BuiltIns.FirstOrDefault(v =>
                string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
            if (b != null) return b;
            lock (_lock)
            {
                return _userVoices.FirstOrDefault(v =>
                    string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>Resolve with fallback — never returns null.</summary>
        public static MeterVoice Resolve(string? name) =>
            Find(name) ?? Find(DefaultVoiceName)!;

        /// <summary>Replace the user-voice set (called when config loads).</summary>
        public static void SetUserVoices(IEnumerable<MeterVoice>? voices)
        {
            lock (_lock)
            {
                _userVoices = (voices ?? Enumerable.Empty<MeterVoice>())
                    .Where(v => !string.IsNullOrWhiteSpace(v.Name))
                    .Where(v => !BuiltIns.Any(b =>
                        string.Equals(b.Name, v.Name, StringComparison.OrdinalIgnoreCase)))
                    .Select(v => { v.BuiltIn = false; return v; })
                    .ToList();
            }
        }

        /// <summary>Snapshot of the user voices (for persistence).</summary>
        public static List<MeterVoice> GetUserVoices()
        {
            lock (_lock) { return _userVoices.Select(v => v.Clone()).ToList(); }
        }

        /// <summary>
        /// Add or update a user voice. Returns the name actually stored: a
        /// name colliding with a built-in is suffixed (" 2", " 3", …) rather
        /// than shadowing — the shared vocabulary stays shared.
        /// </summary>
        public static string SaveUserVoice(MeterVoice voice)
        {
            string name = string.IsNullOrWhiteSpace(voice.Name) ? "My Voice" : voice.Name.Trim();
            lock (_lock)
            {
                string candidate = name;
                int suffix = 2;
                while (BuiltIns.Any(b => string.Equals(b.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                    candidate = $"{name} {suffix++}";
                voice.Name = candidate;
                voice.BuiltIn = false;
                _userVoices.RemoveAll(v =>
                    string.Equals(v.Name, candidate, StringComparison.OrdinalIgnoreCase));
                _userVoices.Add(voice);
                return candidate;
            }
        }

        /// <summary>Remove a user voice by name. Built-ins cannot be removed.</summary>
        public static bool RemoveUserVoice(string name)
        {
            lock (_lock)
            {
                return _userVoices.RemoveAll(v =>
                    string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
            }
        }

        /// <summary>
        /// Partial amplitudes of a rectangular pulse with the given duty cycle
        /// (Fourier series: a_n ∝ sin(π·n·duty)/n). 10% duty reads thin and
        /// nasal, 50% is a square wave — one number, a whole timbre axis.
        /// </summary>
        public static float[] PartialsFromPulseWidth(float duty, int count)
        {
            duty = Math.Clamp(duty, 0.01f, 0.5f);
            var partials = new float[Math.Max(count, 1)];
            for (int n = 1; n <= partials.Length; n++)
                partials[n - 1] = (float)(Math.Sin(Math.PI * n * duty) / n);
            // Normalise so the strongest partial is 1.
            float max = partials.Max(Math.Abs);
            if (max > 0)
                for (int i = 0; i < partials.Length; i++) partials[i] /= max;
            return partials;
        }

        /// <summary>Square-wave partials: odd harmonics at 1/n.</summary>
        public static float[] PartialsFromSquare(int count)
        {
            var partials = new float[Math.Max(count, 1)];
            for (int n = 1; n <= partials.Length; n++)
                partials[n - 1] = (n % 2 == 1) ? 1f / n : 0f;
            return partials;
        }

        /// <summary>
        /// Map a legacy <see cref="WaveformType"/> to the equivalent voice
        /// name, for migrating anything that stored the old enum.
        /// </summary>
        public static string FromLegacyWaveform(WaveformType waveform) => waveform switch
        {
            WaveformType.Square => "Square",
            WaveformType.Sawtooth => "Reedy",
            WaveformType.SlowPulse => "Pulsing",
            WaveformType.FastPulse => "Urgent",
            WaveformType.Alternating => "Ring",
            _ => "Pure",
        };
    }
}
