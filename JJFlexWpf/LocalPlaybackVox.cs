using System;
using System.Threading.Tasks;
using Radios;

namespace JJFlexWpf;

/// <summary>
/// Holds VOX (or CW break-in) off while a computer recording plays on this
/// computer's speakers, and lets it go when the playback ends however it ends
/// (#787, Tape Deck track 4). Shared by "play from computer"
/// (<see cref="RecorderControls.ToggleComputerPlayAsync"/>) and the recordings
/// dialog's preview, which both play through <see cref="RecordingPlayer"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> Slice playback has paused VOX for a local play since
/// round 4, through the radio's one VOX owner. Computer playback did not: it
/// refused while transmitting, but nothing stopped the microphone hearing the
/// speakers and VOX keying the radio on it. Codex flagged it in the track 4
/// design and the plain reading made it a condition of the build. The pause
/// itself is <see cref="FlexBase.PauseVoxForLocalPlaybackAsync"/>, the same
/// owner and the same off-confirmation the slice recorder uses; this class
/// only ties its lifetime to the player's.
/// </para>
/// <para>
/// <b>Ending is the player's StateChanged</b>, which fires on a stop, on the
/// natural end of the file, on a failure to start, and when another playback
/// replaces this one. Whichever it is, once nothing is playing the pause goes.
/// A pause or resume also raises StateChanged; the player is still playing
/// then, so nothing is released.
/// </para>
/// </remarks>
internal static class LocalPlaybackVox
{
    private static readonly object Gate = new();
    private static FlexBase.LocalPlaybackVoxPause? _held;
    private static bool _subscribed;

    /// <summary>
    /// Pause VOX for a local playback about to start. Null means the radio did
    /// not confirm it off and the caller must not play; with no radio the
    /// answer is <see cref="FlexBase.LocalPlaybackVoxPause.NotNeeded"/>.
    /// </summary>
    internal static async Task<FlexBase.LocalPlaybackVoxPause?> PauseAsync(FlexBase? rig) =>
        rig == null ? FlexBase.LocalPlaybackVoxPause.NotNeeded : await rig.PauseVoxForLocalPlaybackAsync();

    /// <summary>
    /// Keep <paramref name="pause"/> until the player stops. Call after the
    /// playback has started; if it has already ended, the pause goes at once.
    /// </summary>
    internal static void HoldUntilPlaybackEnds(FlexBase.LocalPlaybackVoxPause? pause)
    {
        if (pause == null) return;
        FlexBase.LocalPlaybackVoxPause? previous;
        lock (Gate)
        {
            if (!_subscribed)
            {
                RecordingPlayer.StateChanged += OnPlayerStateChanged;
                _subscribed = true;
            }
            previous = _held;
            _held = pause;
        }
        if (!ReferenceEquals(previous, pause)) previous?.Dispose();
        if (!RecordingPlayer.IsPlaying) Release();
    }

    /// <summary>Let the pause go now. Safe when none is held.</summary>
    internal static void Release()
    {
        FlexBase.LocalPlaybackVoxPause? held;
        lock (Gate)
        {
            held = _held;
            _held = null;
        }
        held?.Dispose();
    }

    /// <summary>True while a playback holds a pause. For tests.</summary>
    internal static bool Holding
    {
        get { lock (Gate) return _held?.Holding == true; }
    }

    private static void OnPlayerStateChanged()
    {
        if (!RecordingPlayer.IsPlaying) Release();
    }
}
