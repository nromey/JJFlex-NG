#nullable enable
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using JJTrace;

namespace Radios;

/// <summary>
/// A pause of VOX (or CW break-in) held while a recording plays on this
/// computer's speakers, so the microphone hearing the speakers cannot key the
/// radio (#787, Tape Deck track 4). Dispose to let it go; disposing twice is
/// harmless. <see cref="FlexBase.LocalPlaybackVoxPause.NotNeeded"/> stands for
/// "there was nothing to pause": no radio is connected.
/// </summary>
public partial class FlexBase
{
    // Includes a released playback during its speaker-drain tail: its VOX
    // lease remains paused until the owner has actually released it.
    private readonly System.Collections.Generic.List<VoxPauseOwner.Lease> _localPlaybackVoxLeases = new();

    /// <summary>The answer to <see cref="PauseVoxForLocalPlaybackAsync"/>.</summary>
    public sealed class LocalPlaybackVoxPause : IDisposable
    {
        private readonly FlexBase? _rig;
        private VoxPauseOwner.Lease? _lease;

        /// <summary>No radio, so no VOX to pause: playback may go ahead and nothing is held.</summary>
        public static LocalPlaybackVoxPause NotNeeded { get; } = new(null, null);

        internal LocalPlaybackVoxPause(FlexBase? rig, VoxPauseOwner.Lease? lease)
        {
            _rig = rig;
            _lease = lease;
        }

        /// <summary>True while this pause still holds a lease of the radio's VOX owner.</summary>
        public bool Holding => Volatile.Read(ref _lease) != null;

        /// <summary>
        /// Give the pause back. VOX comes back on only once the last holder has
        /// let go, and only after the playback's tail has had time to leave the
        /// speakers, the same drain slice playback waits for.
        /// </summary>
        public void Dispose()
        {
            var lease = Interlocked.Exchange(ref _lease, null);
            if (lease == null || _rig == null) return;
            _rig.ReleaseVoxWhenReceive(lease, playbackTail: true);
        }
    }

    /// <summary>
    /// Pause whichever autonomous-keying setting the transmit slice's mode uses
    /// (VOX in a voice mode, break-in in CW) for a playback on this computer's
    /// speakers, through the radio's one VOX owner, and wait for the radio to
    /// confirm it is off. The same pause slice playback takes when it plays
    /// locally (<c>StartPlayLockedAsync</c>), so the two compose: VOX comes
    /// back only when the last of them has finished.
    /// </summary>
    /// <returns>
    /// A held pause; <see cref="LocalPlaybackVoxPause.NotNeeded"/> when no radio
    /// is connected; or null when the radio did not confirm the setting off, in
    /// which case nothing is held and the caller must not play.
    /// </returns>
    /// <remarks>
    /// Built for the recordings dialog's preview and for "play from computer"
    /// (Noel's reading of the track 4 design, 2026-10-24: "the dialog's preview
    /// must take the same VOX lease as slice playback, through the shared
    /// owner"). It never enables anything and never keys the radio.
    /// </remarks>
    public async Task<LocalPlaybackVoxPause?> PauseVoxForLocalPlaybackAsync()
    {
        var radio = theRadio;
        if (radio?.Connected != true) return LocalPlaybackVoxPause.NotNeeded;
        var setting = radio.TransmitSlice?.DemodMode == "CW" ? VoxSetting.CwBreakIn : VoxSetting.SimpleVox;
        bool needsOffReport = !LocalPlaybackVoxOff(radio);
        var lease = VoxPauses.Acquire(setting);
        if (lease == null) return null;
        try
        {
            if (needsOffReport)
            {
                // The owner queues its write; a barrier on the same queue makes
                // the reply task visible before it is read.
                await IqQueue(() => { }).ConfigureAwait(false);
                if (!await LastVoxWriteReply(setting, radio, on: false).WaitAsync(SliceVoxOffWait).ConfigureAwait(false))
                {
                    lease.Dispose();
                    return null;
                }
            }
            if (!ReferenceEquals(radio, theRadio) || !LocalPlaybackVoxOff(radio))
            {
                lease.Dispose();
                return null;
            }
            lock (_voxPauseGate)
            {
                _localPlaybackVoxLeases.RemoveAll(p => !p.Paused);
                if (!ReferenceEquals(radio, theRadio))
                {
                    lease.Dispose();
                    return null;
                }
                _localPlaybackVoxLeases.Add(lease);
            }
            return new LocalPlaybackVoxPause(this, lease);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or OperationCanceledException)
        {
            Tracing.TraceLine("PauseVoxForLocalPlaybackAsync: " + ex.Message, TraceLevel.Warning);
            lease.Dispose();
            return null;
        }
    }
}
