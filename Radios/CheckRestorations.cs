using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Radios
{
    /// <summary>What one restore attempt achieved (#835, fix round 4).</summary>
    public enum RestoreOutcome
    {
        /// <summary>Put back.</summary>
        Done,

        /// <summary>Not put back: a refusal, an unconfirmed reply, a changed connection. Reported, never retried.</summary>
        Failed,

        /// <summary>Not put back YET: the radio was transmitting when the write was about to go out. Waits for receive and tries again.</summary>
        Retry,
    }

    /// <summary>
    /// Everything an Audio Check changed on one radio, with the action that puts
    /// each back, run only while that radio reports receive (#835, fix round 3).
    /// One per <see cref="FlexBase"/>: <see cref="FlexBase.CheckRestorations"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it lives with the radio.</b> Until round 3 the duty to put things
    /// back lived on the Check your audio window and its session, so it was
    /// lost whenever either ended first: a check whose session had finished, or
    /// whose window had closed, left work a later disconnect could not see; and
    /// each restore path decided for itself whether it was safe to write. The
    /// Loopback arrangement's own restore never asked, and put the operator's
    /// power back on the prior antenna while a hardware PTT line still held the
    /// radio keyed. Here there is one owner, it lives as long as the radio, and
    /// it asks one question before every write.
    /// </para>
    /// <para>
    /// <b>The rule.</b> A restore runs only while the radio reports it is not
    /// transmitting, asked again immediately before EACH restore. If the radio
    /// is transmitting at that moment the restore waits. It is retried on the
    /// radio's receive report and on a slow poll, so a missed report cannot
    /// leave it waiting forever. Withholding power from a radio that never
    /// reports receive is correct, and <see cref="WithheldByTransmit"/> says so;
    /// nothing here claims a restore it did not make.
    /// </para>
    /// <para>
    /// <b>Held entries.</b> A check registers a restore when it makes a change
    /// (the dummy load it engaged, the power it capped) but must not have it run
    /// before the check has even keyed, so such an entry is held until the check
    /// releases it at unkey. A teardown releases everything held: whatever ends
    /// the check, the owner still puts its changes back.
    /// </para>
    /// <para>
    /// Restores run in the order they were registered, one at a time, off the UI
    /// thread. A restore that throws is traced and counts as not done
    /// (<see cref="CompletionAsync"/> answers false), never as done.
    /// </para>
    /// </remarks>
    public sealed class CheckRestorations : IDisposable
    {
        /// <summary>How often a waiting restore asks the radio again, besides its receive report.</summary>
        public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

        /// <summary>One change to put back.</summary>
        public sealed class Entry
        {
            internal Entry(string name, Func<Task<RestoreOutcome>> restore, bool held)
            {
                Name = name;
                Restore = restore;
                _held = held;
            }

            public string Name { get; }
            internal Func<Task<RestoreOutcome>> Restore { get; }
            internal volatile bool _held;
            internal readonly TaskCompletionSource<bool> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>True once the restore has run, or failed.</summary>
            public bool Finished => Done.Task.IsCompleted;

            /// <summary>Completes true when the restore ran, false when it threw.</summary>
            public Task<bool> Task => Done.Task;
        }

        private readonly Func<bool> _transmitting;
        private readonly object _gate = new();
        private readonly List<Entry> _entries = new();
        private readonly SemaphoreSlim _pump = new(1, 1);
        private Timer? _poll;
        private int _kickAgain;
        private volatile bool _withheld;
        private bool _disposed;
        private readonly List<Task> _starts = new();
        private int _generation;
        private bool _releasingEverything;

        /// <param name="transmitting">The radio's own transmit report, asked before every restore.</param>
        public CheckRestorations(Func<bool> transmitting)
        {
            _transmitting = transmitting;
        }

        /// <summary>Register a change whose restore cannot report failure; it is done once it has run.</summary>
        public Entry Register(string name, Action restore, bool held = false) =>
            Register(name, () => { restore(); return Task.FromResult(RestoreOutcome.Done); }, held);

        /// <summary>
        /// Register a change whose restore says whether it worked: true is done,
        /// false is a failure the owner reports. There is deliberately no overload
        /// taking a plain Task: a restore that can fail must say so (fix round 4,
        /// where several restores signalled failure by returning normally).
        /// </summary>
        public Entry Register(string name, Func<Task<bool>> restore, bool held = false) =>
            Register(name, async () => await restore().ConfigureAwait(false) ? RestoreOutcome.Done : RestoreOutcome.Failed, held);

        /// <summary>Register a change whose restore may also ask to be retried once the radio is receiving.</summary>
        public Entry Register(string name, Func<Task<RestoreOutcome>> restore, bool held = false)
        {
            var entry = new Entry(name, restore, held);
            lock (_gate)
            {
                if (_disposed) { entry.Done.TrySetResult(false); return entry; }
                // A teardown has released everything: a change registered now,
                // by a start that is rolling itself back, is not held either.
                if (_releasingEverything) entry._held = false;
                _entries.Add(entry);
                _generation++;
            }
            if (!entry._held) Kick();
            return entry;
        }

        /// <summary>
        /// A check is STARTING on this radio and may be changing it right now
        /// (#835, fix round 4). Dispose the token when the start has finished,
        /// including any rollback of its own. A teardown waits for every start
        /// in flight before it reads the owner: a start that changes the radio
        /// registers its restores only as it ends, so a teardown that began
        /// mid-start once found the owner empty and reported every change put
        /// back while the start was still changing things.
        /// </summary>
        public IDisposable TrackStart(string name)
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                if (_disposed) return new StartToken(this, done, name, tracked: false);
                _starts.Add(done.Task);
                _generation++;
            }
            return new StartToken(this, done, name, tracked: true);
        }

        private sealed class StartToken : IDisposable
        {
            private readonly CheckRestorations _owner;
            private readonly TaskCompletionSource<bool> _done;
            private readonly string _name;
            private readonly bool _tracked;
            private int _disposed;

            internal StartToken(CheckRestorations owner, TaskCompletionSource<bool> done, string name, bool tracked)
            {
                _owner = owner; _done = done; _name = name; _tracked = tracked;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                if (_tracked) lock (_owner._gate) _owner._starts.Remove(_done.Task);
                _done.TrySetResult(true);
            }

            public override string ToString() => _name;
        }

        /// <summary>
        /// True while a start is in flight, a change has not yet been put back,
        /// or a restore FAILED and no teardown has reported it yet (fix round
        /// 5): a finished failure is still owed to the next teardown, so it is
        /// work, not "nothing to restore". A successful entry leaves the list
        /// as it finishes, so anything still in it is owed.
        /// </summary>
        public bool HasWork { get { lock (_gate) return _starts.Count > 0 || _entries.Count > 0; } }

        /// <summary>
        /// Carry a restoration that has ALREADY failed somewhere else (a start's
        /// own rollback) as a failed entry, so the next teardown reports it.
        /// </summary>
        public Entry RecordFailure(string name)
        {
            var entry = new Entry(name, () => System.Threading.Tasks.Task.FromResult(RestoreOutcome.Failed), held: false);
            JJTrace.Tracing.TraceLine("Check restore NOT confirmed (" + name + ")", System.Diagnostics.TraceLevel.Warning);
            entry.Done.TrySetResult(false);
            lock (_gate)
            {
                if (_disposed) return entry;
                _entries.Add(entry);
                _generation++;
            }
            return entry;
        }

        /// <summary>The change may now be put back (the check has unkeyed).</summary>
        public void Release(Entry? entry)
        {
            if (entry == null || entry.Finished) return;
            entry._held = false;
            Kick();
        }

        /// <summary>Release everything held; a teardown is ending the check whatever its state.</summary>
        public void ReleaseAll()
        {
            lock (_gate) foreach (var e in _entries) e._held = false;
            Kick();
        }

        /// <summary>
        /// For a teardown: release everything held, and everything registered
        /// until <see cref="EndReleasingEverything"/>, since a start caught by
        /// the teardown registers its rollback after this call.
        /// </summary>
        public void BeginReleasingEverything()
        {
            lock (_gate) _releasingEverything = true;
            ReleaseAll();
        }

        /// <summary>The teardown is over; registrations are held as they ask again.</summary>
        public void EndReleasingEverything()
        {
            lock (_gate) _releasingEverything = false;
        }

        /// <summary>True while any change, held or not, has not yet been put back.</summary>
        public bool Pending { get { lock (_gate) return _entries.Any(e => !e.Finished); } }

        /// <summary>True when the last attempt found the radio transmitting and is waiting for it to stop.</summary>
        public bool WithheldByTransmit => _withheld && Pending;

        /// <summary>
        /// Completes when every change registered so far has been handled: true
        /// when every restore ran, false when any threw. Not bounded; a caller
        /// bounds it.
        /// </summary>
        public async Task<bool> CompletionAsync()
        {
            // Starts first, then the changes, and again until nothing new
            // arrived meanwhile: a start's rollback registers its restores as
            // it finishes, after this began waiting (fix round 4).
            bool ok = true;
            while (true)
            {
                int generation;
                Task[] starts;
                Task<bool>[] all;
                lock (_gate)
                {
                    generation = _generation;
                    starts = _starts.ToArray();
                    all = _entries.Select(e => e.Task).ToArray();
                }
                if (starts.Length > 0)
                {
                    await System.Threading.Tasks.Task.WhenAll(starts).ConfigureAwait(false);
                    continue;
                }
                if (all.Length > 0)
                {
                    var results = await System.Threading.Tasks.Task.WhenAll(all).ConfigureAwait(false);
                    if (!results.All(r => r)) ok = false;
                }
                lock (_gate) if (_generation == generation && _starts.Count == 0) return ok;
            }
        }

        /// <summary>Forget failed entries once they have been reported.</summary>
        public void ClearFinished()
        {
            lock (_gate) _entries.RemoveAll(e => e.Finished);
        }

        /// <summary>The radio reported receive, or anything else that should make a waiting restore try again.</summary>
        public void Kick()
        {
            lock (_gate) if (_disposed) return;
            _ = System.Threading.Tasks.Task.Run(PumpAsync);
        }

        private async Task PumpAsync()
        {
            if (!await _pump.WaitAsync(0).ConfigureAwait(false))
            {
                Interlocked.Exchange(ref _kickAgain, 1);
                return;
            }
            try
            {
                while (true)
                {
                    Entry? next;
                    lock (_gate) next = _entries.FirstOrDefault(e => !e.Finished && !e._held);
                    if (next == null) { _withheld = false; break; }

                    // Asked immediately before this restore, every time.
                    bool transmitting;
                    try { transmitting = _transmitting(); }
                    catch (Exception ex)
                    {
                        JJTrace.Tracing.TraceLine("Check restore: the transmit report could not be read: " + ex.Message,
                            System.Diagnostics.TraceLevel.Warning);
                        transmitting = true;
                    }
                    if (transmitting)
                    {
                        if (!_withheld)
                            JJTrace.Tracing.TraceLine("Check restore: withheld while the radio reports transmit (" + next.Name + ")",
                                System.Diagnostics.TraceLevel.Warning);
                        _withheld = true;
                        break;
                    }
                    _withheld = false;
                    RestoreOutcome outcome;
                    try { outcome = await next.Restore().ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        JJTrace.Tracing.TraceLine("Check restore failed (" + next.Name + "): " + ex, System.Diagnostics.TraceLevel.Error);
                        outcome = RestoreOutcome.Failed;
                    }
                    if (outcome == RestoreOutcome.Retry)
                    {
                        // The radio keyed between the owner's question and the
                        // write: stay pending, in order, and wait for receive.
                        JJTrace.Tracing.TraceLine("Check restore: held at its write while the radio reports transmit (" + next.Name + ")",
                            System.Diagnostics.TraceLevel.Warning);
                        _withheld = true;
                        break;
                    }
                    if (outcome == RestoreOutcome.Failed)
                        JJTrace.Tracing.TraceLine("Check restore NOT confirmed (" + next.Name + ")", System.Diagnostics.TraceLevel.Warning);
                    // A failed entry is kept (finished, never pumped again) so
                    // the next CompletionAsync still answers false for it;
                    // ClearFinished forgets it once a teardown has reported it.
                    bool ok = outcome == RestoreOutcome.Done;
                    if (ok) lock (_gate) _entries.Remove(next);
                    next.Done.TrySetResult(ok);
                }
            }
            finally { _pump.Release(); }

            ArmPoll();
            if (Interlocked.Exchange(ref _kickAgain, 0) != 0) Kick();
        }

        /// <summary>Keep a slow poll running while a released restore is waiting, and stop it when none is.</summary>
        private void ArmPoll()
        {
            lock (_gate)
            {
                bool waiting = !_disposed && _entries.Any(e => !e.Finished && !e._held);
                if (waiting && _poll == null)
                    _poll = new Timer(_ => Kick(), null, PollInterval, PollInterval);
                else if (!waiting && _poll != null)
                {
                    _poll.Dispose();
                    _poll = null;
                }
            }
        }

        /// <summary>The radio is gone: nothing more can be restored. What was not restored is reported as not done.</summary>
        public void Dispose()
        {
            List<Entry> left;
            lock (_gate)
            {
                _disposed = true;
                _poll?.Dispose();
                _poll = null;
                left = _entries.Where(e => !e.Finished).ToList();
                _entries.Clear();
            }
            foreach (var e in left)
            {
                JJTrace.Tracing.TraceLine("Check restore abandoned with the radio: " + e.Name, System.Diagnostics.TraceLevel.Warning);
                e.Done.TrySetResult(false);
            }
        }
    }
}
