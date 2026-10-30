using System;
using System.Threading;
using System.Threading.Tasks;

namespace Radios.Listenback;

public sealed record ListenbackOptions(int Rate = 24000, bool SeparatePan = false,
    bool FullDuplex = false, string ReceiveAntenna = null, bool OffAir = false,
    bool HearSelfDuringOffAir = false);

public sealed class ListenbackRun
{
    private readonly Func<Task<RestoreOutcome>> _restore;
    private readonly Action _detach;
    private readonly TaskCompletionSource<bool> _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _restoring = new(1, 1);
    private int _stopping;
    public IqRadioCapture Capture { get; }
    public string DirectoryPath => Capture.Writer.DirectoryPath;
    public bool Restored { get; private set; }

    /// <summary>
    /// True when the last restore attempt wrote nothing more because the radio
    /// was transmitting (our Mox or its interlock), on a connection still
    /// live: the restore is owed, and <see cref="RestoreAgainIfHeldAsync"/>
    /// tries it again once the radio receives (#835, round 1030 repair TD).
    /// </summary>
    public bool RestoreHeldByTransmit { get; private set; }
    public bool HasRadioKeying { get; private set; }
    public bool Active => Volatile.Read(ref _stopping) == 0;
    internal void NoteRadioKeying() => HasRadioKeying = true;

    /// <summary>A run whose restore says whether it was done, refused for transmit (Retry), or failed.</summary>
    internal ListenbackRun(IqRadioCapture capture, Func<Task<RestoreOutcome>> restore, Action detach)
    { Capture = capture; _restore = restore; _detach = detach; }

    /// <summary>A run whose restore answers only yes or no; a no is never retried.</summary>
    internal ListenbackRun(IqRadioCapture capture, Func<Task<bool>> restore, Action detach)
        : this(capture, async () => await restore().ConfigureAwait(false) ? RestoreOutcome.Done : RestoreOutcome.Failed, detach) { }
    public void RadioKeying(bool keyed)
    {
        if (keyed) HasRadioKeying = true;
        Capture.Writer.Mark("radio-TransmitChange", keyed);
    }
    public void OwnKeying(bool keyed) => Capture.Writer.Mark("application-PTT-command", keyed);
    public async Task StopAsync(string reason = "operator-stop")
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) { await _stopped.Task.ConfigureAwait(false); return; }
        try
        {
            _detach();
            try { await Capture.StopAsync(reason).ConfigureAwait(false); }
            finally { await RestoreOnceAsync().ConfigureAwait(false); }
            _stopped.TrySetResult(true);
        }
        catch (Exception ex) { _stopped.TrySetException(ex); throw; }
    }

    /// <summary>
    /// For the radio's restore owner, which runs this only while the radio
    /// reports receive: once the run has stopped, a restore that was held back
    /// because the radio was transmitting is tried again (#835, round 1030
    /// repair TD). Done when the run is restored; Retry when it met a
    /// transmitting radio again; Failed when it was refused for any other
    /// reason, which is never retried here (its record remains, for the
    /// operator's review or the next connect's put-back).
    /// </summary>
    public async Task<RestoreOutcome> RestoreAgainIfHeldAsync()
    {
        await _stopped.Task.ConfigureAwait(false);
        if (Restored) return RestoreOutcome.Done;
        if (!RestoreHeldByTransmit) return RestoreOutcome.Failed;
        return await RestoreOnceAsync().ConfigureAwait(false);
    }

    /// <summary>One restore attempt at a time; a run already restored is not restored again.</summary>
    private async Task<RestoreOutcome> RestoreOnceAsync()
    {
        await _restoring.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Restored) return RestoreOutcome.Done;
            RestoreOutcome outcome;
            try { outcome = await _restore().ConfigureAwait(false); }
            catch { RestoreHeldByTransmit = false; throw; }
            Restored = outcome == RestoreOutcome.Done;
            RestoreHeldByTransmit = outcome == RestoreOutcome.Retry;
            return outcome;
        }
        finally { _restoring.Release(); }
    }
}
