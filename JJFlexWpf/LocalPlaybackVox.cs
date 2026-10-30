using System;
using System.Diagnostics;
using System.Threading.Tasks;
using JJTrace;
using Radios;

namespace JJFlexWpf;

/// <summary>
/// Holds VOX (or CW break-in) off while a computer recording SOUNDS on this
/// computer's speakers, and lets it go when the playback ends however it ends
/// (#787, Tape Deck track 4), and while the playback is paused (#835, fix
/// round 2026-10-30). Shared by "play from computer"
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
/// <b>VOX comes back while the playback is paused</b> (Noel, 2026-10-29 23:58:
/// <i>"A ham might play a recording, pause it, talk about the previous bit on
/// vox, start playback again etc."</i>). A pause gives VOX back once the
/// recording's fade has left the speakers; carrying on goes through
/// <see cref="CarryOnAsync"/>, which pauses VOX again and waits for the radio
/// to confirm it off BEFORE a sample sounds. If the radio does not confirm, or
/// the operator is transmitting, the recording stays paused and the caller
/// says why. The rule is that VOX is never live while audio is coming out of
/// the speakers: a resume that bypasses <see cref="CarryOnAsync"/> is paused
/// again on the spot rather than allowed to sound with VOX on.
/// </para>
/// <para>
/// <b>Ending is the player's StateChanged</b>, which fires on a stop, on the
/// natural end of the file, on a failure to start, on a pause and a resume,
/// and when another playback replaces this one. Once nothing is playing the
/// pause goes and the playback is forgotten.
/// </para>
/// <para>
/// <b>The speakers' tail.</b> A pause or a stop fades the recording out over
/// <see cref="RecordingPlayer.TransportSampleProvider.FadeOutSeconds"/>, and the
/// output device is still playing what it was given for its own latency after
/// that, so VOX is given back only once both have passed
/// (<see cref="SpeakerTailMs"/>). Until 2026-10-30 a stop gave it back at
/// once, while the fade was still audible.
/// </para>
/// </remarks>
internal static class LocalPlaybackVox
{
    private static readonly object Gate = new();
    /// <summary>The VOX pause held while the recording sounds; null while it is paused.</summary>
    private static IDisposable? _held;
    /// <summary>The radio whose VOX the playback paused, kept so carrying on can pause it again.</summary>
    private static FlexBase? _rig;
    /// <summary>A playback this class looks after, sounding or paused.</summary>
    private static bool _owning;
    /// <summary>A carry-on is getting its pause; a second press meanwhile is ignored.</summary>
    private static bool _carryingOn;
    private static bool _subscribed;

    /// <summary>Why a carry-on did or did not happen.</summary>
    internal enum CarryOn
    {
        /// <summary>VOX is paused again and the recording sounds.</summary>
        Resumed,
        /// <summary>Nothing was paused (or it stopped while VOX was being paused).</summary>
        NothingPaused,
        /// <summary>The radio is transmitting, so the recording stays paused.</summary>
        Transmitting,
        /// <summary>The radio did not confirm VOX off, so the recording stays paused.</summary>
        VoxUnconfirmed,
        /// <summary>A carry-on was already under way; this press did nothing.</summary>
        Busy,
    }

    /// <summary>
    /// For tests: stands in for pausing the radio's VOX before a carry-on, so
    /// the order (pause confirmed, THEN sound) can be held without a radio.
    /// </summary>
    internal static Func<FlexBase?, Task<(IDisposable? Pause, CarryOn Refusal)>>? PauseForCarryOnOverride { get; set; }

    /// <summary>For tests: the wait before VOX is given back after a pause or a stop.</summary>
    internal static int? SpeakerTailMsOverride { get; set; }

    /// <summary>
    /// How long after a pause or a stop the recording can still be heard: its
    /// fade, then the output's own latency, then a margin.
    /// </summary>
    internal static int SpeakerTailMs => SpeakerTailMsOverride
        ?? (int)(RecordingPlayer.TransportSampleProvider.FadeOutSeconds * 1000) + EarconPlayer.AlertOutputLatencyMs + 50;

    /// <summary>
    /// Pause VOX for a local playback about to start. Null means the radio did
    /// not confirm it off and the caller must not play; with no radio the
    /// answer is <see cref="FlexBase.LocalPlaybackVoxPause.NotNeeded"/>.
    /// </summary>
    internal static async Task<FlexBase.LocalPlaybackVoxPause?> PauseAsync(FlexBase? rig) =>
        rig == null ? FlexBase.LocalPlaybackVoxPause.NotNeeded : await rig.PauseVoxForLocalPlaybackAsync();

    /// <summary>
    /// Keep <paramref name="pause"/> while the player sounds, and look after the
    /// playback from here on: a pause gives VOX back, and carrying on pauses
    /// it again (<see cref="CarryOnAsync"/>). Call after the playback has
    /// started; if it has already ended, the pause goes at once.
    /// <paramref name="rig"/> is the radio whose VOX was paused, or null with none.
    /// </summary>
    internal static void HoldUntilPlaybackEnds(FlexBase? rig, IDisposable? pause)
    {
        if (pause == null) return;
        IDisposable? previous;
        lock (Gate)
        {
            if (!_subscribed)
            {
                RecordingPlayer.StateChanged += OnPlayerStateChanged;
                _subscribed = true;
            }
            previous = _held;
            _held = pause;
            _rig = rig;
            _owning = true;
        }
        if (!ReferenceEquals(previous, pause)) previous?.Dispose();
        if (!RecordingPlayer.IsPlaying) Release();
        else if (RecordingPlayer.IsPaused) GiveBackForPause();
    }

    /// <summary>
    /// Carry a paused playback on (#835, 2026-10-29 23:58): pause VOX again,
    /// wait for the radio to confirm it off and for any VOX tail to drop, and
    /// only then let the recording sound. A playback this class does not look
    /// after resumes as before. The caller speaks the answer.
    /// </summary>
    internal static async Task<CarryOn> CarryOnAsync()
    {
        FlexBase? rig;
        lock (Gate)
        {
            if (!RecordingPlayer.IsPaused) return CarryOn.NothingPaused;
            if (!_owning)
                return RecordingPlayer.Resume() ? CarryOn.Resumed : CarryOn.NothingPaused;
            if (_carryingOn) return CarryOn.Busy;
            _carryingOn = true;
            rig = _rig;
        }
        try
        {
            var source = PauseForCarryOnOverride ?? PauseForCarryOnLiveAsync;
            (IDisposable? pause, CarryOn refusal) = await source(rig);
            if (pause == null)
            {
                Tracing.TraceLine("LocalPlaybackVox: the paused recording did not carry on: " + refusal, TraceLevel.Info);
                return refusal;
            }
            lock (Gate)
            {
                // Stopped, or replaced, while VOX was being paused.
                if (!_owning || !RecordingPlayer.IsPaused)
                {
                    pause.Dispose();
                    return CarryOn.NothingPaused;
                }
                _held = pause;
            }
            if (!RecordingPlayer.Resume())
            {
                Release();
                return CarryOn.NothingPaused;
            }
            Tracing.TraceLine("LocalPlaybackVox: VOX paused again and confirmed off; the recording carries on",
                TraceLevel.Info);
            return CarryOn.Resumed;
        }
        finally
        {
            lock (Gate) _carryingOn = false;
        }
    }

    /// <summary>
    /// The live pause for a carry-on: refused while the operator holds the
    /// transmitter; otherwise VOX off, confirmed by the radio, and then the
    /// tail of VOX's own hold on the operator's last words allowed to drop,
    /// the same wait a box send makes (<see cref="VoxSendBox.VoxTailDropWait"/>).
    /// A radio still transmitting after that stays the operator's, and the
    /// recording stays paused.
    /// </summary>
    private static async Task<(IDisposable? Pause, CarryOn Refusal)> PauseForCarryOnLiveAsync(FlexBase? rig)
    {
        if (rig == null) return (FlexBase.LocalPlaybackVoxPause.NotNeeded, CarryOn.Resumed);
        if (VoiceMessageSender.OperatorHolds(rig, Dialogs.AudioWorkshopDialog.PttControllerSource?.Invoke()))
            return (null, CarryOn.Transmitting);
        FlexBase.LocalPlaybackVoxPause? pause = await rig.PauseVoxForLocalPlaybackAsync();
        if (pause == null) return (null, CarryOn.VoxUnconfirmed);
        long until = Environment.TickCount64 + (long)VoxSendBox.VoxTailDropWait.TotalMilliseconds;
        while (rig.Transmit && Environment.TickCount64 < until
               && !VoiceMessageSender.OperatorHolds(rig, Dialogs.AudioWorkshopDialog.PttControllerSource?.Invoke()))
            await Task.Delay(50);
        if (rig.Transmit)
        {
            pause.Dispose();
            return (null, CarryOn.Transmitting);
        }
        return (pause, CarryOn.Resumed);
    }

    /// <summary>Let the pause go (after the speakers' tail) and forget the playback. Safe when none is held.</summary>
    internal static void Release()
    {
        IDisposable? held;
        lock (Gate)
        {
            held = _held;
            _held = null;
            _rig = null;
            _owning = false;
        }
        GiveBackAfterTail(held);
    }

    /// <summary>True while a sounding playback holds a pause. For tests.</summary>
    internal static bool Holding
    {
        get
        {
            lock (Gate)
                return _held is FlexBase.LocalPlaybackVoxPause p ? p.Holding : _held != null;
        }
    }

    /// <summary>True while this class looks after a playback, sounding or paused. For tests.</summary>
    internal static bool Owning
    {
        get { lock (Gate) return _owning; }
    }

    /// <summary>The playback paused: give VOX back once the fade has left the speakers.</summary>
    private static void GiveBackForPause()
    {
        IDisposable? held;
        lock (Gate)
        {
            held = _held;
            _held = null;
        }
        if (held == null) return;
        Tracing.TraceLine("LocalPlaybackVox: the recording is paused, so VOX comes back until it carries on",
            TraceLevel.Info);
        GiveBackAfterTail(held);
    }

    private static void GiveBackAfterTail(IDisposable? held)
    {
        if (held == null) return;
        int tail = SpeakerTailMs;
        if (tail <= 0) { held.Dispose(); return; }
        _ = Task.Run(async () =>
        {
            await Task.Delay(tail).ConfigureAwait(false);
            held.Dispose();
        });
    }

    private static void OnPlayerStateChanged()
    {
        if (!RecordingPlayer.IsPlaying) { Release(); return; }
        if (RecordingPlayer.IsPaused) { GiveBackForPause(); return; }
        // Sounding. A playback this class looks after must hold VOX off while
        // it sounds; a resume that came by some other door than CarryOnAsync
        // finds none held, and is paused again rather than sounding with VOX on.
        bool bypassed;
        lock (Gate) bypassed = _owning && !_carryingOn && _held == null;
        if (!bypassed) return;
        Tracing.TraceLine("LocalPlaybackVox: a paused recording resumed without VOX being paused again; paused it",
            TraceLevel.Warning);
        RecordingPlayer.Pause();
    }
}
