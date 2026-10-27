using System;
using System.Threading.Tasks;

namespace Radios;

public partial class FlexBase
{
    private CheckRestorations? _checkRestorations;
    private readonly object _checkRestorationsGate = new();

    /// <summary>
    /// What any Audio Check changed on this radio, and the duty to put it back
    /// (#835, fix round 3). It belongs to the radio, so a check whose session
    /// ended or whose window closed still has its restores carried out and
    /// waited for. Restores run only while this radio reports receive.
    /// </summary>
    public CheckRestorations CheckRestorations
    {
        get
        {
            lock (_checkRestorationsGate)
            {
                if (_checkRestorations != null) return _checkRestorations;
                var owner = new CheckRestorations(() => Transmit);
                // The receive report is the fast road; the owner's own slow poll
                // covers a report that never reaches this subscriber.
                TransmitChange += (_, transmitting) => { if (!transmitting) owner.Kick(); };
                return _checkRestorations = owner;
            }
        }
    }

    private Task<AudioCheckTeardownResult>? _checkTeardown;
    private int _checkTeardownHolders;
    private readonly object _checkTeardownGate = new();

    /// <summary>
    /// True from the moment a teardown starts putting this radio's checks back
    /// until it has finished (#835, fix round 3). While it is true, no new
    /// check may start and the app keys nothing: a teardown that waits must not
    /// let a fresh transmission in behind its own barrier.
    /// </summary>
    public bool TeardownInProgress { get { lock (_checkTeardownGate) return _checkTeardown != null || _checkTeardownHolders > 0; } }

    /// <summary>
    /// Before this radio is torn down on purpose, shut admission: no check may
    /// start and the app keys nothing until every caller has called
    /// <see cref="EndCheckTeardown"/>. A second teardown while one is running
    /// JOINS it (<see cref="SettleChecksForTeardownAsync"/> returns the same
    /// task) rather than starting another.
    /// </summary>
    /// <remarks>
    /// Every caller holds admission shut until it calls
    /// <see cref="EndCheckTeardown"/>, not merely until the task completes, so
    /// a caller still unwinding its wait cannot let input in between the
    /// result and its own next step (disposing the rig).
    /// </remarks>
    public void BeginCheckTeardown()
    {
        // Admission is shut HERE, synchronously, before anything ends or waits.
        lock (_checkTeardownGate) _checkTeardownHolders++;
    }

    /// <summary>
    /// Start settling (or join the settle already running): release every held
    /// restore, then wait, bounded, for all of them and for the command queue.
    /// Call after <see cref="BeginCheckTeardown"/> and after ending whatever is
    /// running, so everything it registers is in the owner first.
    /// </summary>
    public Task<AudioCheckTeardownResult> SettleChecksForTeardownAsync(TimeSpan bound)
    {
        lock (_checkTeardownGate)
        {
            if (_checkTeardown != null) return _checkTeardown;
            var run = new TaskCompletionSource<AudioCheckTeardownResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _checkTeardown = run.Task;
            _ = RunSettleAsync(bound, run);
            return run.Task;
        }
    }

    /// <summary>This caller is done waiting. Admission reopens when every caller is done and the settle has finished.</summary>
    public void EndCheckTeardown()
    {
        lock (_checkTeardownGate)
        {
            if (_checkTeardownHolders > 0) _checkTeardownHolders--;
            if (_checkTeardownHolders == 0 && _checkTeardown?.IsCompleted == true) _checkTeardown = null;
        }
    }

    private async Task RunSettleAsync(TimeSpan bound, TaskCompletionSource<AudioCheckTeardownResult> run)
    {
        AudioCheckTeardownResult result;
        try
        {
            await Task.Yield();
            var owner = CheckRestorations;
            owner.ReleaseAll();
            result = await AudioCheckTeardown.RunAsync(
                settled: owner.CompletionAsync,
                withheld: () => owner.WithheldByTransmit,
                drain: DrainCommandQueueAsync,
                bound: bound).ConfigureAwait(false);
            owner.ClearFinished();
        }
        catch (Exception ex)
        {
            JJTrace.Tracing.TraceLine("SettleChecksForTeardownAsync: " + ex, System.Diagnostics.TraceLevel.Error);
            result = new AudioCheckTeardownResult(false, "failed");
        }
        // Admission reopens only through EndCheckTeardown, once every caller
        // has finished waiting (the retry ladder can unwire and rewire the
        // same radio; a radio being closed is disposed straight after).
        run.TrySetResult(result);
        lock (_checkTeardownGate) if (_checkTeardownHolders == 0) _checkTeardown = null;
    }

    /// <summary>The radio is going: whatever was not put back is abandoned, and said so in the trace.</summary>
    private void DisposeCheckRestorations()
    {
        CheckRestorations? owner;
        lock (_checkRestorationsGate) { owner = _checkRestorations; _checkRestorations = null; }
        owner?.Dispose();
    }
}
