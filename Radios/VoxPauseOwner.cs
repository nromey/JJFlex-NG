#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// Owns temporary pauses of one radio's independent VOX and CW break-in
    /// settings. A lease covers even an already-off setting: a second holder
    /// must participate in the first holder's restore decision.
    /// </summary>
    public sealed class VoxPauseOwner
    {
        private readonly object _gate = new object();
        private readonly Func<FlexBase.VoxSetting, bool> _isOn;
        private readonly Func<FlexBase.VoxSetting, bool, bool, bool> _write;
        private readonly Dictionary<FlexBase.VoxSetting, Pause> _pauses = new();
        private bool _disconnected;
        private readonly Func<Action, Action, bool>? _enqueue;
        private readonly Dictionary<FlexBase.VoxSetting, PendingWrite> _pending = new();

        /// <summary>
        /// Asked at every RESTORE write, the moment it goes out (repair round 3,
        /// rules 1, 2 and 5): null means it may go, otherwise why not. Every VOX
        /// restore, from every holder, goes through this owner, so a route
        /// nobody named (the reflected-power cut's hold given up at teardown,
        /// a send's release, the disconnect's own flush) asks the same
        /// question. Null for an owner built without it.
        /// </summary>
        private readonly Func<string?>? _restoreRefusal;

        /// <summary>
        /// Runs one write's refusal and write as ONE step against the
        /// replacement of the command connection they are pinned to (repair
        /// round 5, Codex's #596 B8): the identity check is made with the
        /// write, so a release whose last check passed cannot then write onto
        /// a new connection. Returns why the pinned target refused (the write
        /// did not run), or what the step returned. Null for an owner built
        /// without it.
        /// </summary>
        private readonly Func<bool, Func<string?>, Func<string?>?, string?>? _atTheWrite;

        /// <summary>
        /// The operator's choices, counted per setting (integration fix 3,
        /// Codex's #596 B3; the rule the mic debt follows): the operator's own
        /// change of a setting voids every restore of it owed before that
        /// change. Each restore carries the count it was owed at, and a restore
        /// whose count is stale refuses: before its write, and again AT the
        /// transport's write, where it is read under this same lock. The
        /// operator records a change here without ever waiting on
        /// <see cref="_gate"/>, which a restore holds while it waits at the
        /// transport, so the record cannot be lost or delayed behind it. A leaf
        /// lock: nothing is done while holding it but the count.
        /// </summary>
        private readonly object _choiceGate = new object();
        private readonly long[] _choice = new long[3];

        /// <summary>
        /// A restore that was owed and withheld at its write: VOX stays off for
        /// now, and the debt is KEPT here, not lost. The next pause of the same
        /// setting inherits it, the disconnect's flush tries it again at its own
        /// write, and an abandon reports it owed.
        /// </summary>
        private readonly Dictionary<FlexBase.VoxSetting, long> _withheld = new();

        private sealed class PendingWrite
        {
            public bool On;
            /// <summary>The operator's choice count this write was decided at.</summary>
            public long Choice;
        }

        internal sealed class Pause
        {
            public int Holders;
            public bool Restore;
            /// <summary>The operator's choice count when this pause was taken: its restore is owed against it.</summary>
            public long Choice;
        }

        private long ChoiceNow(FlexBase.VoxSetting which)
        {
            lock (_choiceGate) return _choice[(int)which];
        }

        /// <summary>The operator has changed <paramref name="which"/> since a restore was owed at <paramref name="choice"/>.</summary>
        private bool OperatorChoseSince(FlexBase.VoxSetting which, long choice) => ChoiceNow(which) != choice;

        private const string OperatorChoseWhy = "the operator has changed it since the restore was owed, and that choice stands";

        /// <summary>Keep a restore owed, unless the operator has chosen since: then nothing is owed.</summary>
        private void KeepOwed(FlexBase.VoxSetting which, long choice)
        {
            if (OperatorChoseSince(which, choice))
            {
                _withheld.Remove(which);
                return;
            }
            _withheld[which] = choice;
        }

        private bool OwedNow(KeyValuePair<FlexBase.VoxSetting, long> entry) => !OperatorChoseSince(entry.Key, entry.Value);

        public VoxPauseOwner(Func<FlexBase.VoxSetting, bool> isOn,
            Func<FlexBase.VoxSetting, bool, bool, bool> write, Func<Action, bool>? enqueue = null,
            Func<string?>? restoreRefusal = null)
            : this(isOn, write, enqueue == null ? null : (action, _) => enqueue(action), restoreRefusal, null) { }

        /// <param name="enqueue">Queues an action, with what to do if the queue
        /// accepted it but ended before running it (integration fix 2).</param>
        /// <param name="atTheWrite">Runs the step pinned, given the write, the
        /// step, and this owner's own question to ask AT the transport's write
        /// (null for an off): whether the operator has chosen since (integration
        /// fix 3). Reads only.</param>
        internal VoxPauseOwner(Func<FlexBase.VoxSetting, bool> isOn,
            Func<FlexBase.VoxSetting, bool, bool, bool> write, Func<Action, Action, bool>? enqueue,
            Func<string?>? restoreRefusal, Func<bool, Func<string?>, Func<string?>?, string?>? atTheWrite)
        {
            _isOn = isOn ?? throw new ArgumentNullException(nameof(isOn));
            _write = write ?? throw new ArgumentNullException(nameof(write));
            _enqueue = enqueue;
            _restoreRefusal = restoreRefusal;
            _atTheWrite = atTheWrite;
        }

        /// <summary>A restore was withheld at its write and is still owed, with no pause holding it.</summary>
        internal bool HasWithheld
        {
            get
            {
                lock (_gate)
                {
                    foreach (var entry in _withheld) if (OwedNow(entry)) return true;
                    return false;
                }
            }
        }

        /// <summary>
        /// Try every withheld restore again (integration fix, 2026-11-06): called
        /// when the radio reports receive on this owner's connection, as the mic
        /// debt is retried. Each goes through the ordinary write, so the restore
        /// question is asked again at its own write; refused again, it is kept
        /// owed again. A setting a pause now holds is that pause's to restore.
        /// </summary>
        internal void RetryWithheld()
        {
            lock (_gate)
            {
                if (_disconnected) return;
                foreach (var entry in new List<KeyValuePair<FlexBase.VoxSetting, long>>(_withheld))
                {
                    var which = entry.Key;
                    if (_pauses.ContainsKey(which)) continue;
                    _withheld.Remove(which);
                    // The operator chose since it was owed (integration fix 3):
                    // never write a value older than the operator's last choice.
                    if (!OwedNow(entry))
                    {
                        Tracing.TraceLine("VoxPauseOwner: " + which + " restore NOT retried - " + OperatorChoseWhy
                            + "; nothing is owed", TraceLevel.Info);
                        continue;
                    }
                    if (!Write(which, true, false, entry.Value) && !_withheld.ContainsKey(which))
                        KeepOwed(which, entry.Value);   // not even queued: still owed
                }
            }
        }

        /// <summary>For tests: a restore of <paramref name="which"/> was withheld and is still owed.</summary>
        internal bool RestoreOwed(FlexBase.VoxSetting which)
        {
            lock (_gate) return _withheld.TryGetValue(which, out long choice) && !OperatorChoseSince(which, choice);
        }

        /// <summary>
        /// Takes a pause without ever enabling a setting. Null means the off
        /// write failed, or this owner has disconnected. Check before relying
        /// on the pause; a returned lease is not a radio acknowledgement.
        /// </summary>
        public Lease? Acquire(FlexBase.VoxSetting which)
        {
            lock (_gate)
            {
                if (_disconnected || which == FlexBase.VoxSetting.None) return null;
                if (!_pauses.TryGetValue(which, out var pause))
                {
                    long now = ChoiceNow(which);
                    bool on;
                    // A queued restore the operator has since overruled is not
                    // the setting's state; the radio's own reading is.
                    try
                    {
                        on = _pending.TryGetValue(which, out var pending) && !(pending.On && pending.Choice != now)
                            ? pending.On : _isOn(which);
                    }
                    catch { return null; }
                    if (on && !Write(which, false, false, now)) return null;
                    // A restore withheld earlier passes to this pause: the
                    // setting is off only because it could not be put back.
                    // Not one the operator has overruled since (integration fix 3).
                    bool owed = _withheld.Remove(which, out long owedAt) && owedAt == now;
                    pause = new Pause { Restore = on || owed, Choice = now };
                    _pauses.Add(which, pause);
                }
                pause.Holders++;
                return new Lease(this, which, pause);
            }
        }

        /// <summary>
        /// The operator took this setting back; no holder may undo that choice.
        /// Recorded at once, under the lock a restore's write-time question
        /// reads (integration fix 3, Codex's #596 B3): every restore owed before
        /// now is void from this moment, wherever it is (a pause, a queued
        /// write, a withheld debt, or a write already waiting at the
        /// transport), and refuses at its write. Never waits on a restore in
        /// progress: the tidy-up below is taken only when the owner is free,
        /// and is only tidiness, since every restore asks the count itself.
        /// </summary>
        public void OperatorChanged(FlexBase.VoxSetting which)
        {
            if (which == FlexBase.VoxSetting.None) return;
            lock (_choiceGate) _choice[(int)which]++;
            if (!Monitor.TryEnter(_gate)) return;
            try
            {
                if (_pauses.TryGetValue(which, out var pause)) pause.Restore = false;
                _pending.Remove(which); // a queued application write cannot undo the operator
                _withheld.Remove(which); // nor can a withheld restore
            }
            finally { Monitor.Exit(_gate); }
        }

        /// <summary>
        /// Called before transport teardown. Releases every holder once with
        /// immediate best-effort writes, then invalidates all outstanding leases.
        /// Calling after transport loss is also safe: failed writes are reported,
        /// and old leases can never write into a later connection.
        /// </summary>
        /// <summary>
        /// The connection ends with the radio not confirmed receiving: write
        /// nothing, forget every pause and pending restore, and say whether a
        /// restore was owed (#596, repair round 2, N3).
        /// </summary>
        public bool Abandon()
        {
            lock (_gate)
            {
                if (_disconnected) return false;
                _disconnected = true;
                bool owed = false;
                foreach (var entry in _withheld) owed |= OwedNow(entry);
                foreach (var entry in _pending) owed |= entry.Value.On && !OperatorChoseSince(entry.Key, entry.Value.Choice);
                foreach (var entry in _pauses) owed |= entry.Value.Restore && !OperatorChoseSince(entry.Key, entry.Value.Choice);
                _pending.Clear();
                _pauses.Clear();
                _withheld.Clear();
                return owed;
            }
        }

        /// <returns>True when a restore is STILL owed: one was withheld at its
        /// write, here or earlier, so VOX was not put back (repair round 3).</returns>
        public bool Disconnect()
        {
            lock (_gate)
            {
                if (_disconnected) return false;
                _disconnected = true;
                // Flush an owed restore whose last holder already released,
                // then discard every queued callback before transport teardown.
                // Each write asks at its own moment, a withheld one too.
                var owedBefore = new List<KeyValuePair<FlexBase.VoxSetting, long>>(_withheld);
                _withheld.Clear();
                foreach (var entry in owedBefore) WriteNow(entry.Key, true, true, entry.Value);
                foreach (var entry in _pending)
                    if (entry.Value.On) WriteNow(entry.Key, true, true, entry.Value.Choice);
                _pending.Clear();
                foreach (var entry in _pauses)
                    if (entry.Value.Restore) Write(entry.Key, true, true, entry.Value.Choice);
                _pauses.Clear();
                bool stillOwed = false;
                foreach (var entry in _withheld) stillOwed |= OwedNow(entry);
                _withheld.Clear();
                return stillOwed;
            }
        }

        /// <param name="choice">The operator's choice count the write was
        /// decided at: a restore refuses once the operator has chosen since.</param>
        private bool Write(FlexBase.VoxSetting which, bool on, bool immediate, long choice)
        {
            if (immediate || _enqueue == null)
            {
                _pending.Remove(which);
                return WriteNow(which, on, immediate, choice);
            }

            // The radio echo can still say OFF after the last holder queued
            // ON. A new holder inherits that intended restore, replaces it
            // with OFF, and makes the stale ON callback inert. Validation and
            // the actual property write share the same gate as acquire/release.
            _pending.TryGetValue(which, out var previous);
            var next = new PendingWrite { On = on, Choice = choice };
            _pending[which] = next;
            try
            {
                if (_enqueue(() =>
                {
                    lock (_gate)
                    {
                        if (_disconnected || !_pending.TryGetValue(which, out var current)
                            || !ReferenceEquals(current, next)) return;
                        _pending.Remove(which);
                        WriteNow(which, on, true, next.Choice);
                    }
                },
                () =>
                {
                    // Accepted, but the loop ended before it ran (integration fix
                    // 2): nothing was written, so a restore stays owed.
                    lock (_gate)
                    {
                        if (!_pending.TryGetValue(which, out var current) || !ReferenceEquals(current, next)) return;
                        _pending.Remove(which);
                        if (on) KeepOwed(which, next.Choice);
                    }
                    Tracing.TraceLine("VoxPauseOwner: " + which + "=" + on + " was dropped when the command loop ended"
                        + (on ? "; the restore is kept as still owed" : ""), TraceLevel.Info);
                })) return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("VoxPauseOwner: enqueue threw: " + ex.Message, TraceLevel.Error);
            }
            if (previous != null) _pending[which] = previous;
            else _pending.Remove(which);
            return false;
        }

        private bool WriteNow(FlexBase.VoxSetting which, bool on, bool immediate, long choice)
        {
            // The refusal and the write are one step (repair round 5): asked
            // and written inside the same pinned step, never asked and then
            // written later through whatever connection is current by then.
            string? refused = null;
            bool written = false;
            // The operator chose since this restore was owed (integration fix
            // 3): asked before the write AND at the transport's write, so a
            // change made while the restore waited for the transport still
            // stops it. Reads only.
            bool voided = false;
            string? OperatorOverruled()
            {
                if (!OperatorChoseSince(which, choice)) return null;
                voided = true;
                return OperatorChoseWhy;
            }
            string? Step()
            {
                if (on)
                {
                    refused = OperatorOverruled();
                    if (refused != null) return refused;
                }
                if (on && _restoreRefusal != null)
                {
                    try { refused = _restoreRefusal(); }
                    catch (Exception ex) { refused = "the radio could not be read (" + ex.Message + ")"; }
                    if (refused != null) return refused;
                }
                try { written = _write(which, on, immediate); }
                catch (Exception ex)
                {
                    Tracing.TraceLine("VoxPauseOwner: write threw: " + ex.Message, TraceLevel.Error);
                }
                return null;
            }
            string? pinned;
            try { pinned = _atTheWrite != null ? _atTheWrite(on, Step, on ? OperatorOverruled : null) : Step(); }
            catch (Exception ex) { pinned = "the write could not be made (" + ex.Message + ")"; }
            refused ??= pinned;
            if (on && voided && !written)
            {
                _withheld.Remove(which);
                Tracing.TraceLine("VoxPauseOwner: " + which + " NOT put back on - " + OperatorChoseWhy
                    + "; nothing is owed", TraceLevel.Info);
                return false;
            }
            if (refused != null)
            {
                if (on)
                {
                    KeepOwed(which, choice);
                    Tracing.TraceLine("VoxPauseOwner: " + which + " NOT put back on - " + refused
                        + ". It stays off, and the restore is kept as still owed.", TraceLevel.Warning);
                }
                else
                    Tracing.TraceLine("VoxPauseOwner: " + which + "=off NOT written - " + refused, TraceLevel.Warning);
                return false;
            }
            if (written) return true;
            // Not written (integration fix 2, Codex's F1): a restore stays owed
            // until one is actually written.
            if (on) KeepOwed(which, choice);
            Tracing.TraceLine("VoxPauseOwner: " + which + "=" + on + " was not issued"
                + (on ? "; the restore is kept as still owed" : ""), TraceLevel.Warning);
            return false;
        }

        public sealed class Lease : IDisposable
        {
            private readonly VoxPauseOwner _owner;
            private readonly FlexBase.VoxSetting _which;
            private readonly Pause _pause;
            private bool _released;

            internal Lease(VoxPauseOwner owner, FlexBase.VoxSetting which, Pause pause)
            {
                _owner = owner;
                _which = which;
                _pause = pause;
            }

            /// <summary>Whether this pause still owns an eventual restore.</summary>
            public bool Paused
            {
                get
                {
                    lock (_owner._gate)
                        return !_released && !_owner._disconnected && _pause.Restore
                            && !_owner.OperatorChoseSince(_which, _pause.Choice);
                }
            }

            /// <returns>True only when this release actually issued the last holder's on write.</returns>
            public bool Release(bool immediate = false)
            {
                lock (_owner._gate)
                {
                    if (_released) return false;
                    _released = true;
                    if (_owner._disconnected) return false;
                    if (--_pause.Holders != 0) return false;
                    _owner._pauses.Remove(_which);
                    // Not when the operator has chosen since this pause was taken (integration fix 3).
                    return _pause.Restore && !_owner.OperatorChoseSince(_which, _pause.Choice)
                        && _owner.Write(_which, true, immediate, _pause.Choice);
                }
            }

            public void Dispose() => Release();
        }
    }
}
