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
            Func<FlexBase.VoxSetting, bool, bool, bool> write, Func<Action, bool>? enqueue = null)
        {
            _isOn = isOn ?? throw new ArgumentNullException(nameof(isOn));
            _write = write ?? throw new ArgumentNullException(nameof(write));
            _enqueue = enqueue;
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
                    pause = new Pause { Restore = on };
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
            }
        }

        /// <summary>
        /// Called before transport teardown. Releases every holder once with
        /// immediate best-effort writes, then invalidates all outstanding leases.
        /// Calling after transport loss is also safe: failed writes are reported,
        /// and old leases can never write into a later connection.
        /// </summary>
        public void Disconnect()
        {
            lock (_gate)
            {
                if (_disconnected) return;
                _disconnected = true;
                // Flush an owed restore whose last holder already released,
                // then discard every queued callback before transport teardown.
                foreach (var entry in _pending)
                    if (entry.Value.On) WriteNow(entry.Key, true, true);
                _pending.Clear();
                foreach (var entry in _pauses)
                    if (entry.Value.Restore) Write(entry.Key, true, true);
                _pauses.Clear();
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
            try
            {
                if (_write(which, on, immediate)) return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("VoxPauseOwner: write threw: " + ex.Message, TraceLevel.Error);
            }
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
