#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private readonly Func<Action, bool>? _enqueue;
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
        private readonly Func<Func<string?>, string?>? _atTheWrite;

        /// <summary>
        /// A restore that was owed and withheld at its write: VOX stays off for
        /// now, and the debt is KEPT here, not lost. The next pause of the same
        /// setting inherits it, the disconnect's flush tries it again at its own
        /// write, and an abandon reports it owed.
        /// </summary>
        private readonly HashSet<FlexBase.VoxSetting> _withheld = new();

        private sealed class PendingWrite
        {
            public bool On;
        }

        internal sealed class Pause
        {
            public int Holders;
            public bool Restore;
        }

        public VoxPauseOwner(Func<FlexBase.VoxSetting, bool> isOn,
            Func<FlexBase.VoxSetting, bool, bool, bool> write, Func<Action, bool>? enqueue = null,
            Func<string?>? restoreRefusal = null)
            : this(isOn, write, enqueue, restoreRefusal, null) { }

        internal VoxPauseOwner(Func<FlexBase.VoxSetting, bool> isOn,
            Func<FlexBase.VoxSetting, bool, bool, bool> write, Func<Action, bool>? enqueue,
            Func<string?>? restoreRefusal, Func<Func<string?>, string?>? atTheWrite)
        {
            _isOn = isOn ?? throw new ArgumentNullException(nameof(isOn));
            _write = write ?? throw new ArgumentNullException(nameof(write));
            _enqueue = enqueue;
            _restoreRefusal = restoreRefusal;
            _atTheWrite = atTheWrite;
        }

        /// <summary>For tests: a restore of <paramref name="which"/> was withheld and is still owed.</summary>
        internal bool RestoreOwed(FlexBase.VoxSetting which)
        {
            lock (_gate) return _withheld.Contains(which);
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
                    bool on;
                    try { on = _pending.TryGetValue(which, out var pending) ? pending.On : _isOn(which); }
                    catch { return null; }
                    if (on && !Write(which, false, false)) return null;
                    // A restore withheld earlier passes to this pause: the
                    // setting is off only because it could not be put back.
                    bool owed = _withheld.Remove(which);
                    pause = new Pause { Restore = on || owed };
                    _pauses.Add(which, pause);
                }
                pause.Holders++;
                return new Lease(this, which, pause);
            }
        }

        /// <summary>The operator took this setting back; no holder may undo that choice.</summary>
        public void OperatorChanged(FlexBase.VoxSetting which)
        {
            lock (_gate)
            {
                if (_pauses.TryGetValue(which, out var pause)) pause.Restore = false;
                _pending.Remove(which); // a queued application write cannot undo the operator
                _withheld.Remove(which); // nor can a withheld restore
            }
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
                bool owed = _withheld.Count > 0;
                foreach (var entry in _pending) owed |= entry.Value.On;
                foreach (var entry in _pauses) owed |= entry.Value.Restore;
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
                var owedBefore = new List<FlexBase.VoxSetting>(_withheld);
                _withheld.Clear();
                foreach (var which in owedBefore) WriteNow(which, true, true);
                foreach (var entry in _pending)
                    if (entry.Value.On) WriteNow(entry.Key, true, true);
                _pending.Clear();
                foreach (var entry in _pauses)
                    if (entry.Value.Restore) Write(entry.Key, true, true);
                _pauses.Clear();
                bool stillOwed = _withheld.Count > 0;
                _withheld.Clear();
                return stillOwed;
            }
        }

        private bool Write(FlexBase.VoxSetting which, bool on, bool immediate)
        {
            if (immediate || _enqueue == null)
            {
                _pending.Remove(which);
                return WriteNow(which, on, immediate);
            }

            // The radio echo can still say OFF after the last holder queued
            // ON. A new holder inherits that intended restore, replaces it
            // with OFF, and makes the stale ON callback inert. Validation and
            // the actual property write share the same gate as acquire/release.
            _pending.TryGetValue(which, out var previous);
            var next = new PendingWrite { On = on };
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
                        WriteNow(which, on, true);
                    }
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

        private bool WriteNow(FlexBase.VoxSetting which, bool on, bool immediate)
        {
            // The refusal and the write are one step (repair round 5): asked
            // and written inside the same pinned step, never asked and then
            // written later through whatever connection is current by then.
            string? refused = null;
            bool written = false;
            string? Step()
            {
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
            try { pinned = _atTheWrite != null ? _atTheWrite(Step) : Step(); }
            catch (Exception ex) { pinned = "the write could not be made (" + ex.Message + ")"; }
            refused ??= pinned;
            if (refused != null)
            {
                if (on)
                {
                    _withheld.Add(which);
                    Tracing.TraceLine("VoxPauseOwner: " + which + " NOT put back on - " + refused
                        + ". It stays off, and the restore is kept as still owed.", TraceLevel.Warning);
                }
                else
                    Tracing.TraceLine("VoxPauseOwner: " + which + "=off NOT written - " + refused, TraceLevel.Warning);
                return false;
            }
            if (written) return true;
            Tracing.TraceLine("VoxPauseOwner: " + which + "=" + on + " was not issued", TraceLevel.Warning);
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
                get { lock (_owner._gate) return !_released && !_owner._disconnected && _pause.Restore; }
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
                    return _pause.Restore && _owner.Write(_which, true, immediate);
                }
            }

            public void Dispose() => Release();
        }
    }
}
