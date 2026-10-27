using System;
using System.Threading.Tasks;
using JJTrace;

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

    /// <summary>
    /// Run restore writes so that each QUEUED write asks the radio again at
    /// the moment it runs (#835, fix round 4): <paramref name="writes"/> calls
    /// ordinary setters, which enqueue; under the ambient
    /// <see cref="StationConnect.QueuedWriteGate"/> each of those is refused at
    /// its run if the radio then reports transmit. Waits for the queue to reach
    /// them, then says what happened: Done, Retry (a write met a transmitting
    /// radio; the owner waits for receive and runs the whole set again, which
    /// is why every set must be safe to repeat), or Failed (a write threw, or the
    /// queue never reached them).
    /// </summary>
    /// <remarks>
    /// Before this, the owner asked the radio before an entry and the entry's
    /// setters then queued writes that went out unasked, so a hardware line or
    /// VOX keying in between got the operator's power while keyed.
    /// </remarks>
    public async Task<RestoreOutcome> RestoreWritesAtReceiveAsync(string name, Action writes)
    {
        var gate = StationConnect.QueuedWriteGate.Open(() => Transmit ? "the radio reports transmit" : null);
        try { writes(); }
        finally { gate.Dispose(); }
        if (!await DrainCommandQueueAsync(RestoreWriteWait).ConfigureAwait(false))
        {
            Tracing.TraceLine("Check restore " + name + ": the command queue did not reach its writes", System.Diagnostics.TraceLevel.Warning);
            return RestoreOutcome.Failed;
        }
        if (gate.Refused > 0) return RestoreOutcome.Retry;
        return gate.Threw > 0 ? RestoreOutcome.Failed : RestoreOutcome.Done;
    }

    /// <summary>How long a set of restore writes may take to reach the radio.</summary>
    internal TimeSpan RestoreWriteWait { get; set; } = TimeSpan.FromSeconds(5);

    // ── The admission boundary (#835, fix round 4) ──────────────────────
    //
    // While TeardownInProgress, nothing APP-INITIATED may key this radio or
    // start a transmit-side resource, whichever door it came through. The
    // refusal lives HERE, at the chokepoints every such door ends at, not in
    // the doors: round 3 refused only in the PTT controller's PttDown and
    // ToggleLock, and voice messages (Transmit, TxFileStartForVox), tune
    // (TxTune), CW (SendCW, and the queued senders) and the IQ bench starts
    // (StartListenbackAsync) walked straight past it.
    //
    // THE CHOKEPOINTS: Transmit going true (and again when its queued write
    // runs), TxTune going true (likewise), SendCW (both forms, and the queued
    // CW senders at their run), TxFileStart, TxFileStartForVox, and
    // StartListenbackAsync. An UNKEY is never refused: Transmit false and
    // TxTune false always pass.
    //
    // ANY NEW SEND MUST END AT ONE OF THESE. A sender that keys the radio some
    // other way (a new FlexLib property, a raw command) has to add the same
    // refusal at its own write, or it reopens the hole this closes. Upper
    // doors keep their own refusals where they have better words to say
    // (PttSafetyController, Check your audio's Start); this one also speaks,
    // throttled, so a door with no refusal of its own is not silent.

    private long _lastTeardownRefusalSpokenMs;

    /// <summary>True, traced and spoken, when a teardown holds admission and <paramref name="what"/> would start a transmission or a resource.</summary>
    internal bool RefusedForCheckTeardown(string what)
    {
        if (!TeardownInProgress) return false;
        Tracing.TraceLine("Refused during an Audio Check teardown: " + what, System.Diagnostics.TraceLevel.Warning);
        long now = Environment.TickCount64;
        if (now - System.Threading.Interlocked.Read(ref _lastTeardownRefusalSpokenMs) > 1500)
        {
            System.Threading.Interlocked.Exchange(ref _lastTeardownRefusalSpokenMs, now);
            // DRAFT PROSE (#835): awaiting Noel.
            ScreenReaderOutput.Speak(Lexicon.Get("audio.check.teardown_refuses"), VerbosityLevel.Critical, interrupt: true);
        }
        return true;
    }

    /// <summary>The same question at a queued write's run, traced and not spoken: it was asked for before the teardown began.</summary>
    internal bool TeardownHeldSilently(string what)
    {
        if (!TeardownInProgress) return false;
        Tracing.TraceLine("Held at its run during an Audio Check teardown: " + what, System.Diagnostics.TraceLevel.Warning);
        return true;
    }

    /// <summary>The radio is going: whatever was not put back is abandoned, and said so in the trace.</summary>
    private void DisposeCheckRestorations()
    {
        CheckRestorations? owner;
        lock (_checkRestorationsGate) { owner = _checkRestorations; _checkRestorations = null; }
        owner?.Dispose();
    }
}
