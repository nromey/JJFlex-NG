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
                // Our own cached Mox is NOT enough (fix round 2026-10-28): FlexLib
                // flips it the moment WE ask for the unkey, before the radio has
                // done anything. The radio's own interlock report decides too.
                var owner = new CheckRestorations(() => Transmit || RadioInterlockReportsTransmit);
                // The receive report is the fast road; the owner's own slow poll
                // covers a report that never reaches this subscriber.
                TransmitChange += (_, transmitting) => { if (!transmitting) owner.Kick(); };
                return _checkRestorations = owner;
            }
        }
    }

    /// <summary>
    /// True while the RADIO's own interlock report says it is keyed or on its
    /// way into or out of transmit (#835, fix round 2026-10-28). Unlike
    /// <see cref="Transmit"/>, nothing on this side sets it: FlexLib's Mox
    /// cache flips the moment we ask for an unkey, while the interlock moves
    /// only when the radio says so. A missing radio or an unknown state reads
    /// as not keyed, which is what the cached Mox already covered.
    /// </summary>
    public bool RadioInterlockReportsTransmit
    {
        get
        {
            var radio = theRadio;
            if (radio == null) return false;
            return radio.InterlockState is Flex.Smoothlake.FlexLib.InterlockState.PTTRequested
                or Flex.Smoothlake.FlexLib.InterlockState.Transmitting
                or Flex.Smoothlake.FlexLib.InterlockState.UnkeyRequested;
        }
    }

    /// <summary>
    /// After a teardown's restore writes have left the command queue, ask the
    /// radio to answer a <c>ping</c> (#835, fix round 2026-10-28). The radio
    /// answers commands in the order it received them, so its answer means it
    /// has processed every write sent before it; "completed" then rests on the
    /// radio's word, not on our queue having handed the writes to the socket.
    /// False when there is no radio, the send is refused, the answer is a
    /// rejection, or none comes inside <paramref name="bound"/>.
    /// </summary>
    public async Task<bool> ConfirmWritesReachedRadioAsync(TimeSpan bound)
    {
        var radio = theRadio;
        if (radio == null) return false;
        var answered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // NOT a bare "ping" (Codex round 1): FlexLib's SendCommand, whenever any
        // telnet trace topic is selected, takes the first word with
        // Substring(0, IndexOf(' ')) and throws on a one-word command. Status
        // taps hold topics (an IQ take; a send piece's PTT-source watch), so a
        // teardown during one would have been refused before the socket. This
        // is the form FlexLib's own keepalive sends, which the radio answers.
        string ping = "ping ms_timestamp=" + (Environment.TickCount64 & 0x7FFFFFFF).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string? refusal = SendRadioCommandWithReply(radio, ping, reply => answered.TrySetResult(reply.Acknowledged));
        if (refusal != null)
        {
            Tracing.TraceLine("Check restore: the radio was not asked to confirm: " + refusal, System.Diagnostics.TraceLevel.Warning);
            return false;
        }
        var finished = await Task.WhenAny(answered.Task, Task.Delay(bound)).ConfigureAwait(false);
        bool ok = ReferenceEquals(finished, answered.Task) && answered.Task.Result;
        Tracing.TraceLine("Check restore: the radio " + (ok ? "answered after the restore writes" : "did not confirm the restore writes"),
            ok ? System.Diagnostics.TraceLevel.Info : System.Diagnostics.TraceLevel.Warning);
        return ok;
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
            // Every disconnect comes through here (fix round 4, must-fix). With
            // no start in flight and nothing to put back there is nothing to
            // wait for: no drain, no wait, and so nothing for the caller to
            // say. A drain on a queue already stopped would otherwise fail and
            // tell the operator a check that never ran was not put back.
            if (_checkRestorations?.HasWork != true)
                return Task.FromResult(new AudioCheckTeardownResult(true, "nothing-to-restore"));
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
            owner.BeginReleasingEverything();
            try
            {
                result = await AudioCheckTeardown.RunAsync(
                    settled: owner.CompletionAsync,
                    withheld: () => owner.WithheldByTransmit,
                    drain: DrainCommandQueueAsync,
                    bound: bound,
                    confirm: ConfirmWritesReachedRadioAsync).ConfigureAwait(false);
            }
            finally { owner.EndReleasingEverything(); }
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
    /// <para>
    /// The question at the run is the owner's own: our cached Mox OR the
    /// radio's interlock (fix round 1030, after Codex's B2 on the put-back).
    /// Until then it asked only Mox, which FlexLib flips the moment we ask and
    /// which never sees a key that is not ours, so a write could meet a
    /// hardware key with Mox still false and go out.
    /// </para>
    /// </remarks>
    public async Task<RestoreOutcome> RestoreWritesAtReceiveAsync(string name, Action writes)
    {
        // Each write's own reply decides (round 1030 repairs, after Codex's B3):
        // while this is open, the setters' writes go out through the
        // reply-bearing path (EnqueueSettingWrite), and Done needs every one of
        // them acknowledged. Until then a write counted as done once the queue
        // had run it, and the teardown's one ping, which proves the radio
        // processed the writes and not that it took them, called it restored.
        var replies = RestoreWriteReplies.Open();
        var gate = StationConnect.QueuedWriteGate.Open(() =>
            Transmit ? "the radio reports transmit"
            : RadioInterlockReportsTransmit ? "the radio's interlock reports transmit"
            : null);
        try { writes(); }
        finally { gate.Dispose(); replies.Dispose(); }
        if (!await DrainCommandQueueAsync(RestoreWriteWait).ConfigureAwait(false))
        {
            Tracing.TraceLine("Check restore " + name + ": the command queue did not reach its writes", System.Diagnostics.TraceLevel.Warning);
            return RestoreOutcome.Failed;
        }
        if (gate.Refused > 0) return RestoreOutcome.Retry;
        if (gate.Threw > 0) return RestoreOutcome.Failed;
        string failure = await replies.FirstFailureAsync(RestoreWriteWait).ConfigureAwait(false);
        if (failure != null)
        {
            Tracing.TraceLine("Check restore " + name + ": NOT restored, " + failure, System.Diagnostics.TraceLevel.Warning);
            return RestoreOutcome.Failed;
        }
        return RestoreOutcome.Done;
    }

    /// <summary>
    /// Queue a radio write the way a setter always has, through
    /// <paramref name="viaSetter"/>. While a restore is collecting replies on
    /// this thread (<see cref="RestoreWritesAtReceiveAsync"/>), the write goes
    /// out instead as <paramref name="command"/>, the command FlexLib's setter
    /// would send, through the reply-bearing path, and the restore waits for
    /// that reply. FlexLib's setter discards the reply and caches the value
    /// before the radio has taken it, so it cannot tell a rejection from a
    /// success; the raw command leaves FlexLib's cache to the radio's own
    /// report. The queue's run-time transmit refusal applies either way.
    /// </summary>
    private void EnqueueSettingWrite(FunctionDel viaSetter, string command, string name = null)
    {
        var replies = RestoreWriteReplies.Ambient;
        if (replies == null) { q.Enqueue(viaSetter, name); return; }
        q.Enqueue((FunctionDel)(() => SendRestoreWrite(replies, command)), name);
    }

    /// <summary>On the command loop: send one restore write with its reply, and give the reply to the restore waiting for it.</summary>
    private void SendRestoreWrite(RestoreWriteReplies replies, string command)
    {
        var reply = new TaskCompletionSource<StationConnect.CommandReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        string refusal = SendRadioCommandWithReply(theRadio, command, r => reply.TrySetResult(r));
        if (refusal != null) replies.NotSent(command, refusal);
        else replies.Sent(command, reply.Task);
    }

    /// <summary>
    /// The replies a restore's writes are owed, collected on the restoring
    /// thread while <see cref="RestoreWritesAtReceiveAsync"/> runs its setters.
    /// </summary>
    private sealed class RestoreWriteReplies : IDisposable
    {
        [ThreadStatic] private static RestoreWriteReplies t_ambient;
        private readonly RestoreWriteReplies _previous;
        private readonly object _gate = new();
        private readonly System.Collections.Generic.List<(string Command, Task<StationConnect.CommandReply> Reply)> _sent = new();
        private string _notSent;

        private RestoreWriteReplies() { _previous = t_ambient; t_ambient = this; }

        internal static RestoreWriteReplies Open() => new();
        internal static RestoreWriteReplies Ambient => t_ambient;
        public void Dispose() { if (ReferenceEquals(t_ambient, this)) t_ambient = _previous; }

        internal void Sent(string command, Task<StationConnect.CommandReply> reply) { lock (_gate) _sent.Add((command, reply)); }
        internal void NotSent(string command, string why) { lock (_gate) _notSent ??= "'" + command + "' was not sent: " + why; }

        /// <summary>Null when every write was sent and the radio acknowledged each; otherwise the first reason it was not.</summary>
        internal async Task<string> FirstFailureAsync(TimeSpan bound)
        {
            (string Command, Task<StationConnect.CommandReply> Reply)[] sent;
            lock (_gate)
            {
                if (_notSent != null) return _notSent;
                sent = _sent.ToArray();
            }
            var deadline = DateTime.UtcNow + bound;
            foreach (var (command, reply) in sent)
            {
                var left = deadline - DateTime.UtcNow;
                if (left > TimeSpan.Zero) await Task.WhenAny(reply, Task.Delay(left)).ConfigureAwait(false);
                if (!reply.IsCompletedSuccessfully) return WriteNotAnswered(command);
                var r = reply.Result;
                if (!r.Acknowledged) return WriteRejected(command, r);
            }
            return null;
        }
    }

    /// <summary>The trace words for a write the radio never answered; shared by the put-back and the check's own restores.</summary>
    private static string WriteNotAnswered(string command) => "'" + command + "' went out and the radio did not answer it";

    /// <summary>The trace words for a write the radio rejected, with its code; shared by the put-back and the check's own restores.</summary>
    private static string WriteRejected(string command, StationConnect.CommandReply reply) =>
        "the radio rejected '" + command + "' (" + reply.Code.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)
            + (reply.Text.Length > 0 ? " " + reply.Text : "") + ")";

    /// <summary>
    /// An armed test tone or reference moved the transmit input to this
    /// computer; put it back through the restore owner (#835, fix round 5), so
    /// a teardown waits for the write rather than letting it be stranded in a
    /// command queue that is about to stop. Asked again at its run, like every
    /// restore here.
    /// </summary>
    public CheckRestorations.Entry RestoreTransmitInputAfterInjection(string had) =>
        CheckRestorations.Register("injected source: transmit input", () => RestoreWritesAtReceiveAsync("transmit input", () =>
        {
            using (AppInitiatedSettingChanges()) MicSource = had;
        }));

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
    // runs), TxTune going true (likewise), FlexTunerOn going true (and its
    // queued TXTune and ATUTuneStart writes at their run), SendCW (both forms,
    // and the queued CW senders at their run), TxFileStart, TxFileStartForVox,
    // TxToneStart and TxToneStartAsync, and StartListenbackAsync. An UNKEY is
    // never refused: Transmit false, TxTune false, FlexTunerOn false and the
    // tuner bypass always pass.
    //
    // SWEPT in fix round 5 for raw writes that bypass these: every
    // theRadio.Mox, theRadio.TXTune and ATUTuneStart write in FlexBase, every
    // cwx.Send, the transmit-file and tone starts, and raw command strings
    // that key (xmit, transmit tune, atu start, cwx). The only raw ones were
    // the Transmit and TxTune setters themselves (guarded) and FlexTunerOn's
    // two queued writes (now guarded). TransmitKillSwitch.RaiseCarrier, used
    // by the Fixer, goes through the Transmit and TxTune setters.
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
