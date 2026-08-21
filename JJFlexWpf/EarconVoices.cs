using System;
using System.Collections.Generic;

namespace JJFlexWpf
{
    /// <summary>
    /// Which set of alert-voice definitions is live (#147).
    ///
    /// Noel asked for a setting that selects "the original sine-based sounds or
    /// the new ones", and the honest way to offer that is not a global bypass
    /// that turns every earcon into a beep — it is a second, complete set of
    /// definitions for the same seven words. Every call site keeps asking for
    /// <c>EarconVoices.Press</c>; only what Press MEANS changes. So a sound's
    /// pitch, cadence, length and loudness tier are identical across the two
    /// sets, and the only variable is timbre and envelope — which is exactly
    /// the comparison an operator is trying to make.
    /// </summary>
    public enum EarconVoiceSet
    {
        /// <summary>The rebuilt set: shaped attack, decay, harmonics, and a
        /// warning family that escalates on three axes. Sprint 32 Track E.</summary>
        Modern = 0,

        /// <summary>The set as it sounded before that rebuild.</summary>
        Classic = 1,
    }

    /// <summary>
    /// The alert path's voice vocabulary — <see cref="MeterVoice"/> instances
    /// authored for one-shot earcons rather than for continuous meter
    /// sonification.
    ///
    /// Sprint 32 Track E, #112. Before this the assembly held three additive
    /// synthesisers that did not know about each other: the real engine
    /// (<see cref="MeterVoice"/> + <see cref="VoicedToneSampleProvider"/>), a
    /// hand-rolled gavel that was unwired for four months, and a crude
    /// fundamental-plus-integer-partials helper inside EarconPlayer. Three
    /// vocabularies for one idea meant three places to author a tone and three
    /// sets of parameters to learn. There is now one, and this file is where
    /// its alert-side words live.
    ///
    /// These are deliberately NOT added to <see cref="MeterVoiceLibrary.BuiltIns"/>.
    /// That list is the operator-facing meter-voice alphabet, chosen so that
    /// fifteen identities stay separable in a picker; alert voices are chosen
    /// for a different job (a 60 ms answer to a keypress) and would only make
    /// the meter picker longer without making it better. Voices are first-class
    /// data — anyone can construct one — so nothing is lost by keeping the two
    /// vocabularies in one grammar but two dictionaries.
    ///
    /// Why the earcons get an envelope at all: Noel, 2026-08-19 — "for some
    /// tones I'd also consider adding more of a fade out (decay)... you might
    /// use it for a button press." A sine gated on and off with symmetric
    /// linear fades is the same sound backwards as forwards, and a sound with
    /// no attack-decay shape reads as a machine artefact rather than as an
    /// answer. Decay is what makes a short tone feel struck.
    ///
    /// Sprint 33 Track F, #147: every voice below now exists TWICE — once as
    /// the rebuilt Sprint 32 definition and once as the definition it replaced —
    /// and <see cref="ActiveSet"/> decides which one the seven public accessors
    /// hand back. Nothing outside this file knows there are two.
    /// </summary>
    public static class EarconVoices
    {
        // ------------------------------------------------------------------
        // #147 — the set selector
        // ------------------------------------------------------------------

        private static volatile EarconVoiceSet _activeSet = EarconVoiceSet.Modern;

        /// <summary>
        /// Which set the seven accessors resolve against. Defaults to
        /// <see cref="EarconVoiceSet.Modern"/>, so an operator who never opens
        /// the setting hears exactly what shipped.
        ///
        /// Read on every earcon, written from Settings. A plain volatile field
        /// is enough: a voice reference is swapped wholesale (the MeterVoice
        /// contract), so the worst a race can do is play one sound from the
        /// set the operator just left.
        /// </summary>
        public static EarconVoiceSet ActiveSet
        {
            get => _activeSet;
            set => _activeSet = value;
        }

        /// <summary>The set names as an operator reads them, in enum order.
        /// Index matches <see cref="EarconVoiceSet"/>.</summary>
        public static IReadOnlyList<string> SetLabels { get; } = new[]
        {
            "Modern — shaped tones with harmonics",
            "Classic — plain tones, as they sounded before",
        };

        private static bool Classic => _activeSet == EarconVoiceSet.Classic;

        // ------------------------------------------------------------------
        // The seven words. Each resolves through the active set; nothing that
        // plays a sound needs to know the setting exists.
        // ------------------------------------------------------------------

        /// <summary>
        /// Near-sine with a whisper of second harmonic. The replacement for a
        /// bare <c>SignalGenerator</c> sine: same pitch reading, but the tiny
        /// upper partial and the shaped attack give it a body that survives
        /// being played over receive audio. Sustains for the whole note.
        ///
        /// Classic: the bare sine it replaced.
        /// </summary>
        public static MeterVoice Plain => Classic ? ClassicPlain : ModernPlain;

        /// <summary>
        /// The button press. Fast strike, then it gets out of the way — the
        /// caller sets <see cref="MeterVoice.DecayMs"/> to the note length via
        /// <see cref="DecayingOver"/> so the tone lands and falls away inside
        /// its own duration instead of stopping dead.
        ///
        /// Classic: a sine that stops dead, because that is what it did.
        /// </summary>
        public static MeterVoice Press => Classic ? ClassicPress : ModernPress;

        /// <summary>
        /// The confirmation ding: fundamental plus a soft octave, a long tail.
        /// Replaces the hand-rolled DingToneSampleProvider, which was a fourth
        /// synthesiser doing exactly this and nothing else.
        ///
        /// Classic: that hand-rolled ding's own spectrum — fundamental plus
        /// octave, no third partial, perfectly harmonic. This is one of the two
        /// voices where Classic is NOT a bare sine, because the sound it is
        /// reproducing was never a bare sine.
        /// </summary>
        public static MeterVoice Chime => Classic ? ClassicChime : ModernChime;

        /// <summary>
        /// The warning alarm's timbre, carried over unchanged from the
        /// hand-built version Noel specified and approved on 2026-08-19:
        /// fundamental with the 2nd and 3rd harmonics stacked underneath at
        /// falling gain. What it gains here is a real envelope — a 25 ms swell
        /// in and a release out, rather than two symmetric linear ramps — and
        /// equal-power normalisation, so its loudness is directly comparable
        /// with every other earcon for the first time.
        ///
        /// Classic: the same partials with the symmetric linear ramps it had
        /// before normalisation. The other voice whose Classic form is not a
        /// bare sine — the alarm was already additive when it was written.
        /// </summary>
        public static MeterVoice Alarm => Classic ? ClassicAlarm : ModernAlarm;

        /// <summary>
        /// First warning — a nudge. Mellow, steady, no pattern. Says "you are
        /// still transmitting" without saying anything is wrong yet.
        ///
        /// Classic: <c>Beep(800, 150)</c>, which is to say a sine.
        /// </summary>
        public static MeterVoice WarningCalm => Classic ? ClassicSine : ModernWarningCalm;

        /// <summary>
        /// Second warning — insistent. Brighter spectrum, and it pulses twice
        /// inside its own duration (60 ms on, 40 ms off against a 200 ms note),
        /// so the repetition is audible without the tone getting longer.
        ///
        /// Classic: a sine. In the original family the only thing separating
        /// the three warnings was pitch, and selecting Classic restores that
        /// — including the weakness #118 was written to fix.
        /// </summary>
        public static MeterVoice WarningInsistent => Classic ? ClassicSine : ModernWarningInsistent;

        /// <summary>
        /// Last warning — the "oh crap". Inharmonic partials give it a metallic
        /// edge that nothing else in the app has, and a 35/25 ms gate hammers
        /// rather than pulses. Deliberately unpleasant: it fires when the next
        /// thing the operator does depends on hearing it.
        ///
        /// Classic: a sine, one step higher than the last one.
        /// </summary>
        public static MeterVoice WarningUrgent => Classic ? ClassicSine : ModernWarningUrgent;

        // ------------------------------------------------------------------
        // Modern definitions — Sprint 32 Track E, unchanged
        // ------------------------------------------------------------------

        private static MeterVoice ModernPlain { get; } = new MeterVoice
        {
            Name = "Alert Plain",
            Description = "Clean tone with a little warmth",
            Partials = new[] { 1f, 0.12f },
            AttackMs = 6f,
            SustainLevel = 1f,
        };

        private static MeterVoice ModernPress { get; } = new MeterVoice
        {
            Name = "Alert Press",
            Description = "Struck tone that falls away",
            Partials = new[] { 1f, 0.28f, 0.1f },
            AttackMs = 3f,
            SustainLevel = 0f,
        };

        private static MeterVoice ModernChime { get; } = new MeterVoice
        {
            Name = "Alert Chime",
            Description = "Bright ding with a ringing tail",
            Partials = new[] { 1f, 0.25f, 0.08f },
            Inharmonicity = 0.01f,
            AttackMs = 2f,
            SustainLevel = 0f,
        };

        private static MeterVoice ModernAlarm { get; } = new MeterVoice
        {
            Name = "Alert Alarm",
            Description = "Sustained tone with harmonics, unmistakably not a toggle",
            Partials = new[] { 1f, 0.35f, 0.18f },
            AttackMs = 25f,
            SustainLevel = 1f,
        };

        // ------------------------------------------------------------------
        // The PTT warning family (#118). Warning1Beep and Beep were byte for
        // byte the same call, and the escalation from first nudge to last
        // resort was one sine getting higher — 800, 1000, 1200 Hz. Pitch alone
        // is the weakest axis available: an operator has to have heard the
        // other two recently to know which one this is.
        //
        // So the family now escalates on three axes at once. Timbre goes from
        // mellow to bright to harsh. Pattern goes from one steady tone, to a
        // tone that pulses twice, to one that hammers. Loudness climbs a tier
        // at each step. Any one of those is enough to place the sound without
        // a reference, which is the whole point of a warning.
        // ------------------------------------------------------------------

        private static MeterVoice ModernWarningCalm { get; } = new MeterVoice
        {
            Name = "Alert Warning Calm",
            Description = "Mellow steady nudge",
            Partials = new[] { 1f, 0.18f },
            AttackMs = 18f,
            SustainLevel = 1f,
        };

        private static MeterVoice ModernWarningInsistent { get; } = new MeterVoice
        {
            Name = "Alert Warning Insistent",
            Description = "Brighter, pulses twice",
            Partials = new[] { 1f, 0.55f, 0.3f, 0.15f },
            AttackMs = 4f,
            DecayMs = 40f,
            SustainLevel = 0.6f,
            GateOnMs = 60f,
            GateOffMs = 40f,
        };

        private static MeterVoice ModernWarningUrgent { get; } = new MeterVoice
        {
            Name = "Alert Warning Urgent",
            Description = "Harsh metallic hammering",
            Partials = new[] { 1f, 0.7f, 0.55f, 0.4f, 0.3f },
            Inharmonicity = 0.05f,
            Brightness = 0.15f,
            AttackMs = 2f,
            DecayMs = 20f,
            SustainLevel = 0.7f,
            GateOnMs = 35f,
            GateOffMs = 25f,
        };

        // ------------------------------------------------------------------
        // Classic definitions — what the same seven words meant before the
        // Sprint 32 rebuild.
        //
        // This is a RECONSTRUCTION, not a blanket "everything becomes a sine".
        // Five of the seven really were a bare sine gated on and off with
        // symmetric 10 ms linear fades, and for those ClassicSine is the honest
        // answer. Two were not: the confirmation ding was a hand-rolled
        // fundamental-plus-octave with a tail, and the warning alarm was
        // already additive when Noel specified it on 2026-08-19. Turning those
        // two into sines would not be "the original sounds", it would be a
        // third set nobody asked for.
        //
        // The 10 ms attack is the one detail worth naming: the old path used
        // FadeInOutSampleProvider with a 10 ms linear in and out, and the
        // engine's own activation fade is 10 ms, so an attack of 10 with a full
        // sustain reproduces the old shape closely enough that the difference
        // is not the thing being judged.
        // ------------------------------------------------------------------

        private static MeterVoice ClassicSine { get; } = new MeterVoice
        {
            Name = "Classic Sine",
            Description = "Plain sine, gated on and off",
            Partials = new[] { 1f },
            AttackMs = 10f,
            SustainLevel = 1f,
        };

        private static MeterVoice ClassicPlain => ClassicSine;

        private static MeterVoice ClassicPress { get; } = new MeterVoice
        {
            Name = "Classic Press",
            Description = "Plain sine that stops dead",
            Partials = new[] { 1f },
            AttackMs = 10f,
            SustainLevel = 1f,
        };

        private static MeterVoice ClassicChime { get; } = new MeterVoice
        {
            Name = "Classic Chime",
            Description = "Fundamental and octave, with a tail",
            Partials = new[] { 1f, 0.25f },
            AttackMs = 2f,
            SustainLevel = 0f,
        };

        private static MeterVoice ClassicAlarm { get; } = new MeterVoice
        {
            Name = "Classic Alarm",
            Description = "Sustained tone with harmonics, symmetric ramps",
            Partials = new[] { 1f, 0.35f, 0.18f },
            AttackMs = 10f,
            SustainLevel = 1f,
        };

        /// <summary>
        /// A copy of <paramref name="baseVoice"/> whose decay is stretched to
        /// fill a note of <paramref name="durationMs"/>, so the tone reaches
        /// <paramref name="sustainLevel"/> exactly as the note ends. This is
        /// how a one-shot earcon gets a fade-out that scales with its own
        /// length instead of a fixed tail that is too long on short sounds and
        /// too short on long ones.
        ///
        /// In the Classic set this is deliberately a no-op beyond the clone.
        /// The decay shape IS part of what #147 lets an operator switch off —
        /// applying it to a Classic voice would give a plain sine a modern
        /// envelope, which is neither set. The one exception is a voice that
        /// asks for a zero sustain in its own definition (Classic Chime), which
        /// keeps the tail it always had.
        ///
        /// Always returns a clone. Built-in voices are shared data and are
        /// never edited in place — the same contract meters follow.
        /// </summary>
        public static MeterVoice DecayingOver(MeterVoice baseVoice, int durationMs, float sustainLevel = 0f)
        {
            var v = (baseVoice ?? Plain).Clone();
            if (Classic && v.SustainLevel > 0f) return v;
            v.DecayMs = Math.Max(durationMs - v.AttackMs, 1f);
            v.SustainLevel = Math.Clamp(sustainLevel, 0f, 1f);
            return v;
        }

        // ==================================================================
        // #145 — the CW keying spectrum
        // ==================================================================

        /// <summary>
        /// One named CW keying timbre: a display name and the spectrum behind
        /// it. Stored in config by <see cref="Id"/>, which is stable and never
        /// translated; <see cref="Label"/> is what an operator reads.
        /// </summary>
        public sealed class CwWaveform
        {
            public string Id { get; init; } = "";
            public string Label { get; init; } = "";
            public string Description { get; init; } = "";

            /// <summary>The spectrum. Null means a single unshaped sine, which
            /// is what the notifier has always produced — so the default costs
            /// nothing and changes nothing.</summary>
            public MeterVoice? Voice { get; init; }
        }

        /// <summary>
        /// The CW keying vocabulary (#145). Noel, 2026-08-19: "recommend
        /// allowing the user to change CW generation sound type if it's hard to
        /// hear with band noise. Now sine, allow for square, saw, and the
        /// harmonics you've implemented."
        ///
        /// WHY THESE AND NOT THE WHOLE VOICE LIBRARY. A CW mark is not a meter
        /// tone. Its envelope is fixed by physics and by the ARRL click
        /// recommendation — a raised-cosine rise and fall of a few milliseconds
        /// — and its length is fixed by PARIS timing at the operator's WPM.
        /// So the CW renderer takes a voice's SPECTRUM (partials, brightness,
        /// inharmonicity) and nothing else: attack, decay, sustain, gating,
        /// tremolo and pitch alternation are all meaningless or actively
        /// destructive on a 60 ms dit. A gated voice would chop a dit into
        /// fragments; a 300 ms attack would swallow it whole.
        ///
        /// That is also why this is a curated list rather than the fifteen
        /// meter voices: the ones that are left out are left out because their
        /// identity lives entirely in the parameters CW cannot use.
        ///
        /// The spectra themselves are built from the shared grammar —
        /// <see cref="MeterVoiceLibrary.PartialsFromSquare"/> and
        /// <see cref="MeterVoiceLibrary.PartialsFromPulseWidth"/> — so there is
        /// still exactly one place in the assembly that knows what a square
        /// wave is made of. See #112.
        /// </summary>
        public static IReadOnlyList<CwWaveform> CwWaveforms { get; } = new[]
        {
            new CwWaveform
            {
                Id = "Sine",
                Label = "Sine",
                Description = "A single pure tone. What CW notifications have always used.",
                Voice = null,
            },
            new CwWaveform
            {
                Id = "Square",
                Label = "Square",
                Description = "Odd harmonics, hollow and buzzy. The loudest-sounding option "
                            + "at the same measured level, because most of a square wave's "
                            + "energy sits above the fundamental.",
                Voice = new MeterVoice
                {
                    Name = "CW Square",
                    Partials = MeterVoiceLibrary.PartialsFromSquare(9),
                },
            },
            new CwWaveform
            {
                Id = "Sawtooth",
                Label = "Sawtooth",
                Description = "Every harmonic, falling smoothly. Brassy — the richest of "
                            + "the set and the hardest for band noise to hide.",
                Voice = new MeterVoice
                {
                    Name = "CW Sawtooth",
                    Partials = SawtoothPartials(12),
                },
            },
            new CwWaveform
            {
                Id = "Reed",
                Label = "Reed",
                Description = "Energy concentrated a couple of harmonics up, like an oboe. "
                            + "Nasal, and it sits in a part of the spectrum band hiss does not.",
                Voice = new MeterVoice
                {
                    Name = "CW Reed",
                    Partials = new[] { 0.6f, 1f, 0.85f, 0.7f, 0.5f, 0.35f, 0.25f, 0.15f },
                },
            },
            new CwWaveform
            {
                Id = "Hollow",
                Label = "Hollow",
                Description = "Odd harmonics with a steep rolloff, like a clarinet. Woody "
                            + "and warm — richer than a sine without being harsh.",
                Voice = new MeterVoice
                {
                    Name = "CW Hollow",
                    Partials = new[] { 1f, 0f, 0.45f, 0f, 0.28f, 0f, 0.18f, 0f, 0.12f },
                },
            },
            new CwWaveform
            {
                Id = "Bell",
                Label = "Bell",
                Description = "Slightly stretched harmonics give it a metallic edge no other "
                            + "sound in the application has, which is the point: it will not "
                            + "be mistaken for received CW or for your own sidetone.",
                Voice = new MeterVoice
                {
                    Name = "CW Bell",
                    Partials = new[] { 1f, 0.7f, 0f, 0.85f, 0f, 0.45f, 0f, 0.3f },
                    Inharmonicity = 0.04f,
                },
            },
        };

        /// <summary>The default CW waveform id — the sound that shipped.</summary>
        public const string DefaultCwWaveformId = "Sine";

        /// <summary>
        /// Resolve a stored id to a waveform. An unknown id (hand-edited config,
        /// a name retired in a later version) falls back to the plain sine
        /// rather than refusing to make a sound.
        /// </summary>
        public static CwWaveform ResolveCwWaveform(string? id)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                foreach (var w in CwWaveforms)
                    if (string.Equals(w.Id, id, StringComparison.OrdinalIgnoreCase))
                        return w;
            }
            return CwWaveforms[0];
        }

        /// <summary>
        /// Sawtooth partial amplitudes: every harmonic at 1/n. The one spectrum
        /// in the CW set that <see cref="MeterVoiceLibrary"/> has no helper for
        /// — square and pulse are there, saw is not. Written here rather than
        /// added to that class only because a second Sprint 33 track is moving
        /// the meter UI onto a new model and this file is not on its list; it
        /// belongs beside PartialsFromSquare and should move there after the
        /// merge train.
        /// </summary>
        private static float[] SawtoothPartials(int count)
        {
            var partials = new float[Math.Max(count, 1)];
            for (int n = 1; n <= partials.Length; n++) partials[n - 1] = 1f / n;
            return partials;
        }
    }
}
