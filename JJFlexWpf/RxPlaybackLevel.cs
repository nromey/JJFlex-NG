using System;

namespace JJFlexWpf
{
    /// <summary>
    /// What happens to the radio's audio on this computer while a recording
    /// plays back here. The numbers are the drop in dB, so a persisted name
    /// that fails to parse can be told from a real one.
    /// </summary>
    public enum PlaybackRadioLevel
    {
        Unchanged = 0,
        Lowered6 = 6,
        Lowered12 = 12,
        Lowered18 = 18,
        Lowered24 = 24,
        Muted = 100,
    }

    /// <summary>
    /// Lowers or mutes the radio's audio on this computer while a recording
    /// plays back, so the recording is not heard on top of the band (Noel,
    /// bench 2026-10-25: "Right now it plays on top").
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One setting, six steps, muted included.</b> Noel asked for a mute
    /// checkbox, then for the drop to be configurable; a single choice covers
    /// both in one tab stop. Muted is the same stage taken to zero gain.
    /// 12 dB is the default: clearly behind the recording, still loud enough
    /// to hear a call.
    /// </para>
    /// <para>
    /// <b>Computer-side only, by the same reasoning as <see cref="RxDuck"/>.</b>
    /// It never touches the radio's own levels, which are shared in MultiFlex
    /// and would be left wrong by a crash. So it reaches audio heard through
    /// this computer and not the rig's own speaker; Noel accepted that limit.
    /// </para>
    /// <para>
    /// <b>NOT the warning duck.</b> RxDuck is warnings-only by Noel's ruling
    /// of 2026-08-21, and WarningDuckScopeTests holds it there. This is a
    /// separate gain stage with its own target, so playback never asks the
    /// warning duck for anything.
    /// </para>
    /// <para>
    /// <b>Owned by <see cref="RecordingPlayer"/>.</b> It begins when a playback
    /// starts and ends on the one path every playback ends through, the same
    /// path that restores the workshop's receive gate. A process that dies
    /// mid-playback leaves nothing behind: the gain lives in this process.
    /// </para>
    /// </remarks>
    public static class RxPlaybackLevel
    {
        public const PlaybackRadioLevel DefaultLevel = PlaybackRadioLevel.Lowered12;

        /// <summary>Glide down to the playback level over this long.</summary>
        internal const float AttackMs = 60f;

        /// <summary>
        /// Glide back to full over this long when playback pauses or ends. Half a
        /// second, on a decibel curve: the first version came back in a straight
        /// line in a quarter of a second, and Noel heard the band jump ("a bit of
        /// a blip as it brings the band up", retest 2026-10-25).
        /// </summary>
        internal const float ReleaseMs = 500f;

        private static volatile int _level = (int)DefaultLevel;
        private static volatile bool _active;
        private static volatile bool _paused;

        /// <summary>The operator's choice, from Settings.</summary>
        public static PlaybackRadioLevel Level
        {
            get => (PlaybackRadioLevel)_level;
            set => _level = (int)value;
        }

        /// <summary>True from a playback's start to its end.</summary>
        public static bool Active => _active;

        internal static void Begin() { _paused = false; _active = true; }

        internal static void End() { _active = false; _paused = false; }

        /// <summary>
        /// A paused playback gives the band back until it resumes (Noel, retest
        /// 2026-10-25: "pausing still should probably bring audio back up").
        /// </summary>
        internal static void SetPaused(bool paused) => _paused = paused;

        /// <summary>The gain the receive pipeline glides toward: 1 unless a recording is sounding.</summary>
        public static float TargetGain => _active && !_paused ? GainFor(Level) : 1f;

        internal static float GainFor(PlaybackRadioLevel level) => level switch
        {
            PlaybackRadioLevel.Unchanged => 1f,
            PlaybackRadioLevel.Muted => 0f,
            _ => (float)Math.Pow(10, -(int)level / 20.0),
        };

        /// <summary>
        /// How deep the chosen level goes, in dB: a full excursion of the glide.
        /// Muted counts as 60 dB, where the glide's floor is.
        /// </summary>
        internal static float DepthDb => Level switch
        {
            PlaybackRadioLevel.Unchanged => 0f,
            PlaybackRadioLevel.Muted => 60f,
            _ => (int)Level,
        };

        /// <summary>The persisted name: "Lowered12", "Muted" and so on.</summary>
        public static string LevelName(PlaybackRadioLevel level) => level.ToString();

        /// <summary>Read a persisted name; anything unrecognised is the default.</summary>
        public static PlaybackRadioLevel ParseLevel(string? name) =>
            Enum.TryParse(name, ignoreCase: false, out PlaybackRadioLevel level) && Enum.IsDefined(level)
                ? level
                : DefaultLevel;

        /// <summary>The Settings label for a choice, from the lexicon.</summary>
        public static string Label(PlaybackRadioLevel level) => Radios.Lexicon.Get(level switch
        {
            PlaybackRadioLevel.Unchanged => "settings.recorder.playback_level_unchanged",
            PlaybackRadioLevel.Lowered6 => "settings.recorder.playback_level_lowered6",
            PlaybackRadioLevel.Lowered18 => "settings.recorder.playback_level_lowered18",
            PlaybackRadioLevel.Lowered24 => "settings.recorder.playback_level_lowered24",
            PlaybackRadioLevel.Muted => "settings.recorder.playback_level_muted",
            _ => "settings.recorder.playback_level_lowered12",
        });

        /// <summary>The Settings choices, in the order they are offered.</summary>
        public static readonly PlaybackRadioLevel[] Choices =
        {
            PlaybackRadioLevel.Unchanged,
            PlaybackRadioLevel.Lowered6,
            PlaybackRadioLevel.Lowered12,
            PlaybackRadioLevel.Lowered18,
            PlaybackRadioLevel.Lowered24,
            PlaybackRadioLevel.Muted,
        };
    }
}
