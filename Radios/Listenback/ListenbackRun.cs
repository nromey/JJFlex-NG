using System;
using System.Threading;
using System.Threading.Tasks;

namespace Radios.Listenback;

public sealed record ListenbackOptions(int Rate = 24000, bool SeparatePan = false,
    bool FullDuplex = false, string ReceiveAntenna = null, bool OffAir = false);

public sealed class ListenbackRun
{
    private readonly Func<Task<bool>> _restore;
    private readonly Action _detach;
    private readonly TaskCompletionSource<bool> _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _stopping;
    public IqRadioCapture Capture { get; }
    public string DirectoryPath => Capture.Writer.DirectoryPath;
    public bool Restored { get; private set; }
    public bool HasRadioKeying { get; private set; }
    public bool Active => Volatile.Read(ref _stopping) == 0;
    internal void NoteRadioKeying() => HasRadioKeying = true;
    internal ListenbackRun(IqRadioCapture capture, Func<Task<bool>> restore, Action detach)
    { Capture = capture; _restore = restore; _detach = detach; }
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
            finally { Restored = await _restore().ConfigureAwait(false); }
            _stopped.TrySetResult(true);
        }
        catch (Exception ex) { _stopped.TrySetException(ex); throw; }
    }
}
