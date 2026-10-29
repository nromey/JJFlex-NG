using System;
using System.Collections.Generic;
using System.Xml.Serialization;

namespace Radios
{
    /// <summary>
    /// The operator's countdown, ONE for the whole app (#835): the count-in
    /// Check your audio plays before it keys, and the count-in the Fix tool's
    /// stages play. Saved app-wide with the audio settings
    /// (<c>AudioOutputConfig.Countdown</c>) and made live through
    /// <see cref="Current"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ruled by Noel 2026-10-28, 21:32 to 21:40.</b> A countdown that can be
    /// turned off; its length, 2 to 10 seconds with 5 the default, one pip a
    /// second and the last beat the long tone; the pip's base frequency, with
    /// presets plus Custom the way the test tone offers them, held to 200 to
    /// 2000 Hz so the long tone an octave above it still plays cleanly; the
    /// pip's duration, with the long tone a fixed multiple of it; and a
    /// "Starting ... in" lead-in that can be turned off. One Countdown group in
    /// Settings, a Play sample button, and the Fix tool on the same settings.
    /// </para>
    /// <para>
    /// <b>Everything is held inside its range on the way IN</b>
    /// (<see cref="Clamped"/>, applied by the <see cref="Current"/> setter), so
    /// a hand-edited file saying 40 seconds or 9 Hz cannot reach the sound or
    /// the keying. Out of range is clamped rather than refused: the operator
    /// meant "longer" or "lower", and the nearest legal value is the honest
    /// reading of that.
    /// </para>
    /// <para>
    /// <b>The beat is not a setting.</b> One pip a second is the ruling, and
    /// the rhythm is what makes the count countable (#396). The pip's DURATION
    /// is the operator's; it is held below the beat so the pips never abut,
    /// and the long tone, twice the pip, still fits inside its own beat.
    /// </para>
    /// </remarks>
    public sealed class CountdownSettings
    {
        /// <summary>Shortest countdown, in seconds (beats).</summary>
        public const int MinSeconds = 2;
        /// <summary>Longest countdown, in seconds (beats).</summary>
        public const int MaxSeconds = 10;
        /// <summary>The default length, ruled 21:34.</summary>
        public const int DefaultSeconds = 5;

        /// <summary>Lowest pip frequency. The long tone is one octave above.</summary>
        public const int MinPipHz = 200;
        /// <summary>Highest pip frequency, so the long tone tops out at 4 kHz.</summary>
        public const int MaxPipHz = 2000;
        /// <summary>
        /// The default pip: the countdown's counting pitch as it has always
        /// been. Pinned equal to <c>EarconPlayer.CountdownCountHz</c> by
        /// <c>CountdownSettingsTests</c>, because that constant is read by the
        /// earcon contract tests and stays where they read it.
        /// </summary>
        public const int DefaultPipHz = 300;

        /// <summary>Shortest pip, in milliseconds.</summary>
        public const int MinPipMs = 50;
        /// <summary>
        /// Longest pip, in milliseconds: half the beat, so the long tone
        /// (<see cref="LandingMultiple"/> times the pip) still ends inside its
        /// own beat, before the key goes down.
        /// </summary>
        public const int MaxPipMs = 500;
        /// <summary>The default pip length: the counting dit as it has always been.</summary>
        public const int DefaultPipMs = 300;

        /// <summary>The long tone's length as a multiple of the pip's.</summary>
        public const int LandingMultiple = 2;

        /// <summary>
        /// The beat: one pip a second (ruled 21:34). Pinned equal to
        /// <c>EarconPlayer.CountdownIntervalMs</c>.
        /// </summary>
        public const int BeatMs = 1000;

        /// <summary>
        /// The named pip frequencies the Countdown group offers before Custom,
        /// in its order. A frequency not in this list is shown as Custom.
        /// </summary>
        public static IReadOnlyList<int> PipHzPresets { get; } = new[] { 300, 500, 800, 1200 };

        /// <summary>
        /// The pip sounds, in the order the Countdown group offers them: the
        /// six CW notification waveforms, by their stored ids (ruled by Noel
        /// 2026-10-29 00:51, after hearing Hollow's pips). The spectra live in
        /// <c>EarconVoices.CwWaveforms</c>; <c>CountdownWiringTests</c> holds
        /// this list equal to theirs, so a sound cannot be offered here that
        /// the earcons cannot play.
        /// </summary>
        public static IReadOnlyList<string> PipSounds { get; } = new[] { "Sine", "Square", "Sawtooth", "Reed", "Hollow", "Bell" };

        /// <summary>
        /// The default pip sound: Hollow, the sound the countdown has always
        /// had. The ruling leaves the default to Noel once he has heard all
        /// six; until he picks, it does not change.
        /// </summary>
        public const string DefaultPipSound = "Hollow";

        /// <summary>Whether Check your audio counts down before it keys. Default on.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>The countdown's length in seconds, which is also its number of beats.</summary>
        public int Seconds { get; set; } = DefaultSeconds;

        /// <summary>The counting pip's frequency in hertz. The long tone is twice this.</summary>
        public int PipHz { get; set; } = DefaultPipHz;

        /// <summary>How long each counting pip sounds, in milliseconds.</summary>
        public int PipMs { get; set; } = DefaultPipMs;

        /// <summary>
        /// Whether the count opens with "Starting (the method) in". Default on:
        /// it names what is about to key. Off, the count is the numbers alone.
        /// </summary>
        public bool LeadIn { get; set; } = true;

        /// <summary>
        /// The sound of the pips and the long tone, one of <see cref="PipSounds"/>
        /// by its id. The Fix tool's countdown uses it too.
        /// </summary>
        public string PipSound { get; set; } = DefaultPipSound;

        /// <summary>The long tone's frequency: one octave above the pip.</summary>
        [XmlIgnore]
        public int LandingHz => PipHz * 2;

        /// <summary>The long tone's length: a fixed multiple of the pip's.</summary>
        [XmlIgnore]
        public int LandingMs => PipMs * LandingMultiple;

        /// <summary>A copy with every value held inside its range.</summary>
        public CountdownSettings Clamped() => new()
        {
            Enabled = Enabled,
            Seconds = Math.Clamp(Seconds, MinSeconds, MaxSeconds),
            PipHz = Math.Clamp(PipHz, MinPipHz, MaxPipHz),
            PipMs = Math.Clamp(PipMs, MinPipMs, MaxPipMs),
            LeadIn = LeadIn,
            PipSound = KnownPipSound(PipSound),
        };

        /// <summary>A plain copy, for a dialog that edits before it commits.</summary>
        public CountdownSettings Clone() => new()
        {
            Enabled = Enabled,
            Seconds = Seconds,
            PipHz = PipHz,
            PipMs = PipMs,
            LeadIn = LeadIn,
            PipSound = PipSound,
        };

        /// <summary>
        /// A stored sound as one of <see cref="PipSounds"/>: the same sound in
        /// any case is that sound, and anything else (a hand-edited file, a
        /// name retired later) is the default. Never silence: a countdown that
        /// makes no sound reads as a check that has not started.
        /// </summary>
        public static string KnownPipSound(string? id)
        {
            if (!string.IsNullOrWhiteSpace(id))
                foreach (var known in PipSounds)
                    if (string.Equals(known, id.Trim(), StringComparison.OrdinalIgnoreCase)) return known;
            return DefaultPipSound;
        }

        private static volatile CountdownSettings _current = new();

        /// <summary>
        /// The live settings every countdown reads. Setting it stores a clamped
        /// COPY, so nothing the caller does afterwards can change a countdown
        /// under way, and a reader always sees one whole set.
        /// </summary>
        public static CountdownSettings Current
        {
            get => _current;
            set => _current = (value ?? new CountdownSettings()).Clamped();
        }
    }
}
