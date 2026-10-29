using System;
using System.Collections.Generic;

namespace Radios
{
    /// <summary>How a countdown is delivered: in words, in tones, or both.</summary>
    public enum CountdownVoicing
    {
        /// <summary>Sounds are off: the count is spoken.</summary>
        Spoken,
        /// <summary>Sounds on, verbosity below Chatty: the count is beeped only.</summary>
        Beeped,
        /// <summary>Sounds on and verbosity Chatty: spoken AND beeped, each number on its pip.</summary>
        SpokenAndBeeped,
    }

    /// <summary>One beat of a countdown: when it falls, what number it is, and how it sounds.</summary>
    /// <param name="AtMs">When the beat falls, in milliseconds from the start of the countdown.</param>
    /// <param name="Number">The number the beat stands for, counting down to 1.</param>
    /// <param name="IsLanding">True for the last beat, the long tone.</param>
    /// <param name="Hz">The tone's frequency.</param>
    /// <param name="Ms">The tone's length.</param>
    /// <param name="Beeped">Whether the tone plays.</param>
    /// <param name="Spoken">What is spoken on this beat, or null.</param>
    /// <param name="Sound">The pip sound, one of <see cref="CountdownSettings.PipSounds"/>. Carried
    /// on the beat so Play sample plays the choice on screen, not the one saved.</param>
    public sealed record CountdownBeat(int AtMs, int Number, bool IsLanding, int Hz, int Ms,
        bool Beeped, string? Spoken, string Sound = CountdownSettings.DefaultPipSound);

    /// <summary>
    /// A countdown worked out in full before it plays (#835): every beat's
    /// time, number, tone and words. Pure, so the shape Noel ruled can be held
    /// by a test with no sound, no screen reader and no clock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The shape.</b> One beat a second, as many beats as the countdown has
    /// seconds; every beat but the last is a pip at the operator's frequency,
    /// and the last is the long tone, an octave up, twice the pip's length and
    /// decaying. Spoken, the numbers count down to 1 on those same beats, so
    /// "5 ... 4 ... 3 ... 2 ... 1", and the check's own "Transmitting" follows
    /// when the key goes down at <see cref="EndsAtMs"/>.
    /// </para>
    /// <para>
    /// <b>The lead-in takes a beat of its own.</b> "Starting the microphone
    /// test in" is spoken at the start and the first number falls one beat
    /// later. Folding it into the first number's utterance would push that
    /// number a second or more behind its pip at an ordinary speech rate, and
    /// the whole point of the spoken-and-beeped mode is that the two agree.
    /// The lead-in is spoken in every voicing when it is on; in the beeped-only
    /// voicing it is followed by the pips, which read naturally as the count.
    /// </para>
    /// <para>
    /// <b>The key goes down one beat after the long tone starts</b>, never
    /// earlier: the long tone is "1", and "transmitting" is the beat after it.
    /// The long tone is held below one beat by <see cref="CountdownSettings"/>,
    /// so it has finished before the transmit tone sounds. This is Check your
    /// audio's rule. The Fix tool keeps its own ruled key-up (on the last
    /// counting pip, so the radio is already up by the long tone, 2026-08-30),
    /// derived in <c>EarconPlayer.CountdownLastDitAtMs</c> from
    /// <see cref="FixerCounts"/>.
    /// </para>
    /// </remarks>
    public sealed class CountdownPlan
    {
        /// <summary>How this countdown is delivered.</summary>
        public CountdownVoicing Voicing { get; }

        /// <summary>The lead-in sentence, spoken at the start, or null when it is off.</summary>
        public string? LeadIn { get; }

        /// <summary>The beats, in order. The last is the long tone.</summary>
        public IReadOnlyList<CountdownBeat> Beats { get; }

        /// <summary>
        /// When the countdown has ended and the key may go down, in milliseconds
        /// from its start, if the setup has kept up. A slower setup moves this
        /// later, never earlier: see <see cref="CountdownRun"/>.
        /// </summary>
        public int EndsAtMs { get; }

        private CountdownPlan(CountdownVoicing voicing, string? leadIn,
            IReadOnlyList<CountdownBeat> beats, int endsAtMs)
        {
            Voicing = voicing;
            LeadIn = leadIn;
            Beats = beats;
            EndsAtMs = endsAtMs;
        }

        /// <summary>
        /// Which voicing applies (ruled 21:32): sounds off, the count is spoken;
        /// sounds on and verbosity Chatty, spoken and beeped; sounds on
        /// otherwise, beeped only. The last case is Claude's reading of the
        /// spec, which named only the first two.
        /// </summary>
        /// <param name="soundsOn">Whether the operator's sound switches let a transmit sound through.</param>
        /// <param name="verbosity">The current speech verbosity.</param>
        public static CountdownVoicing VoicingFor(bool soundsOn, VerbosityLevel verbosity)
        {
            if (!soundsOn) return CountdownVoicing.Spoken;
            return verbosity >= VerbosityLevel.Chatty ? CountdownVoicing.SpokenAndBeeped : CountdownVoicing.Beeped;
        }

        /// <summary>Work a countdown out from the operator's settings.</summary>
        /// <param name="settings">The countdown settings; clamped here, so a raw set is safe.</param>
        /// <param name="voicing">How it is delivered: see <see cref="VoicingFor"/>.</param>
        /// <param name="leadIn">The lead-in sentence, or null for none.</param>
        /// <param name="numberText">The words for a number; the lexicon's count wording when null.</param>
        public static CountdownPlan For(CountdownSettings settings, CountdownVoicing voicing,
            string? leadIn = null, Func<int, string>? numberText = null)
        {
            var s = (settings ?? new CountdownSettings()).Clamped();
            numberText ??= n => Lexicon.Get("audio.countdown.count", ("count", n));
            bool speak = voicing != CountdownVoicing.Beeped;
            bool beep = voicing != CountdownVoicing.Spoken;
            string? lead = string.IsNullOrWhiteSpace(leadIn) ? null : leadIn;
            int first = lead == null ? 0 : CountdownSettings.BeatMs;

            var beats = new List<CountdownBeat>(s.Seconds);
            for (int i = 0; i < s.Seconds; i++)
            {
                int number = s.Seconds - i;
                bool landing = number == 1;
                beats.Add(new CountdownBeat(
                    AtMs: first + i * CountdownSettings.BeatMs,
                    Number: number,
                    IsLanding: landing,
                    Hz: landing ? s.LandingHz : s.PipHz,
                    Ms: landing ? s.LandingMs : s.PipMs,
                    Beeped: beep,
                    Spoken: speak ? numberText(number) : null,
                    Sound: s.PipSound));
            }
            return new CountdownPlan(voicing, lead, beats, first + s.Seconds * CountdownSettings.BeatMs);
        }

        /// <summary>
        /// The Fix tool's figure, which plays as one sound: its counting pips
        /// are every beat but the last, so one fewer than the seconds.
        /// </summary>
        public static int FixerCounts(CountdownSettings settings) =>
            (settings ?? new CountdownSettings()).Clamped().Seconds - 1;
    }
}
