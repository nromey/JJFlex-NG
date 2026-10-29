#nullable enable
using System;
using System.Diagnostics;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// The radio's transmit monitor, switched on for a send and put back
    /// afterwards (#835; Noel, 2026-10-28 16:36 and 16:47).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rulings.</b> 16:36: <i>"if we send stuff to the radio, we probably
    /// should turn monitor on silently and then return it to off if it's
    /// off."</i> 16:47, after hearing a box send's local copy on this computer
    /// far louder than the air: the monitor, not a local copy, for every send,
    /// box on or off, voice message, slice or computer recording, <i>"Same
    /// path"</i>. It is the honest account of what went out, after the radio's
    /// processing and ALC. 16:48: the send uses the operator's monitor LEVEL as
    /// it stands and never touches it; there is no level here at all.
    /// </para>
    /// <para>
    /// <b>Only what it turned on, it turns off.</b> A monitor already on when
    /// the first send began is the operator's and is never written. One the
    /// send turned on goes back off when the last send holding it lets go,
    /// whichever way it ended: sent, stopped, paused, taken over, run long.
    /// </para>
    /// <para>
    /// <b>The operator's hand wins.</b> If the operator touches the monitor
    /// while a send holds it, it is theirs from then on and is left as they
    /// left it: any write through the operator-facing setter
    /// (<see cref="IPort.OperatorWrites"/> moving), or the monitor seen OFF
    /// after it was seen on (another client, or the radio's own panel). So a
    /// monitor the operator turns on mid-send stays on.
    /// </para>
    /// <para>
    /// <b>A radio that went away is not written.</b> The put-back needs the
    /// same connection the turn-on used; after a disconnect it is traced and
    /// skipped, because a write to the next connection would be a write to a
    /// radio nobody asked about.
    /// </para>
    /// <para>
    /// Holds nest, so a box send of a slice recording (the send host's hold
    /// and the slice recorder's own on-air hold) turns it on once and off once.
    /// </para>
    /// </remarks>
    public sealed class SendMonitorOwner
    {
        /// <summary>What the owner needs from the radio, behind an interface so a test can drive it.</summary>
        public interface IPort
        {
            /// <summary>The monitor as the radio stands now, or null when there is no radio to read.</summary>
            bool? MonitorOn { get; }

            /// <summary>
            /// The send's own write. Queued to the radio in order, and never
            /// counted as the operator's.
            /// </summary>
            void WriteMonitor(bool on);

            /// <summary>Counts every write through the operator-facing setter.</summary>
            long OperatorWrites { get; }

            /// <summary>The connection the radio is on now, or null with none; compared by reference.</summary>
            object? Connection { get; }

            /// <summary>
            /// Called with true when the first hold begins and false when the
            /// last ends, so the port can call <see cref="Observe"/> whenever
            /// the radio reports the monitor.
            /// </summary>
            void Watch(bool watching);
        }

        private readonly IPort _port;
        private readonly object _gate = new();
        private int _holders;
        private bool _weTurnedOn;
        private bool _sawOn;
        private bool _operators;
        private long _writesAtTurnOn;
        private object? _connection;

        public SendMonitorOwner(IPort port) { _port = port ?? throw new ArgumentNullException(nameof(port)); }

        /// <summary>True while at least one send holds the monitor.</summary>
        public bool Holding { get { lock (_gate) return _holders > 0; } }

        /// <summary>True while the monitor is on because a send turned it on and nobody has touched it since.</summary>
        public bool OnForASend { get { lock (_gate) return _holders > 0 && _weTurnedOn && !_operators; } }

        /// <summary>
        /// Hold the monitor on for a send until the returned lease is disposed.
        /// <paramref name="who"/> is for the trace only. Disposing twice is harmless.
        /// </summary>
        public IDisposable Hold(string who)
        {
            bool turnOn = false, watch = false;
            lock (_gate)
            {
                if (_holders++ == 0)
                {
                    watch = true;
                    _weTurnedOn = _sawOn = _operators = false;
                    _connection = _port.Connection;
                    if (_port.MonitorOn == false && _connection != null)
                    {
                        _weTurnedOn = true;
                        _writesAtTurnOn = _port.OperatorWrites;
                        turnOn = true;
                    }
                }
            }
            if (watch) SafePort(() => _port.Watch(true), "watch");
            if (turnOn)
            {
                Tracing.TraceLine("SendMonitor: transmit monitor on for " + who + " (it was off)", TraceLevel.Info);
                SafePort(() => _port.WriteMonitor(true), "turn on");
            }
            return new Lease(this);
        }

        /// <summary>
        /// Note what the radio reports. Called by the port whenever the monitor
        /// changes, and before every put-back.
        /// </summary>
        public void Observe()
        {
            lock (_gate) ObserveLocked();
        }

        private void ObserveLocked()
        {
            if (_holders == 0 || !_weTurnedOn || _operators) return;
            if (_port.OperatorWrites != _writesAtTurnOn)
            {
                _operators = true;
                Tracing.TraceLine("SendMonitor: the operator set the monitor during the send; it is left as they set it",
                    TraceLevel.Info);
                return;
            }
            bool? on = _port.MonitorOn;
            if (on == true) _sawOn = true;
            else if (on == false && _sawOn)
            {
                _operators = true;
                Tracing.TraceLine("SendMonitor: the monitor went off during the send, not by us; it is left as found",
                    TraceLevel.Info);
            }
        }

        private void Release()
        {
            bool turnOff = false, unwatch = false, skipped = false;
            lock (_gate)
            {
                if (_holders == 0) return;
                ObserveLocked();
                if (--_holders > 0) return;
                unwatch = true;
                if (_weTurnedOn && !_operators)
                {
                    object? now = _port.Connection;
                    if (now != null && ReferenceEquals(now, _connection)) turnOff = true;
                    else skipped = true;
                }
                _weTurnedOn = _sawOn = _operators = false;
                _connection = null;
            }
            if (unwatch) SafePort(() => _port.Watch(false), "unwatch");
            if (turnOff)
            {
                Tracing.TraceLine("SendMonitor: transmit monitor back off", TraceLevel.Info);
                SafePort(() => _port.WriteMonitor(false), "turn off");
            }
            else if (skipped)
            {
                Tracing.TraceLine("SendMonitor: the radio's connection went away during the send, so the monitor"
                    + " the send turned on was NOT put back off", TraceLevel.Warning);
            }
        }

        private static void SafePort(Action act, string what)
        {
            try { act(); }
            catch (Exception ex)
            {
                Tracing.TraceLine("SendMonitor: " + what + " failed: " + ex.Message, TraceLevel.Warning);
            }
        }

        private sealed class Lease : IDisposable
        {
            private SendMonitorOwner? _owner;
            internal Lease(SendMonitorOwner owner) { _owner = owner; }
            public void Dispose() => System.Threading.Interlocked.Exchange(ref _owner, null)?.Release();
        }
    }
}
