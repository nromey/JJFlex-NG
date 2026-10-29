#nullable enable
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// FlexBase, the transmit monitor a send turns on and puts back (#835,
    /// rulings of 2026-10-28 16:36 and 16:47). The decisions are
    /// <see cref="SendMonitorOwner"/>'s; this is its port onto the radio.
    /// </summary>
    public partial class FlexBase
    {
        private SendMonitorOwner? _sendMonitor;
        private long _monitorOperatorWrites;

        private SendMonitorOwner SendMonitor
        {
            get
            {
                SendMonitorOwner? owner = _sendMonitor;
                if (owner != null) return owner;
                System.Threading.Interlocked.CompareExchange(ref _sendMonitor,
                    new SendMonitorOwner(new SendMonitorPort(this)), null);
                return _sendMonitor!;
            }
        }

        /// <summary>
        /// Hold the radio's transmit monitor on for a send, silently, until the
        /// returned lease is disposed; if the send turned it on, it goes back
        /// off then, unless the operator touched it meanwhile. Never touches
        /// the monitor's level. <paramref name="who"/> is for the trace.
        /// </summary>
        public IDisposable HoldMonitorForSend(string who) => SendMonitor.Hold(who);

        /// <summary>True while the monitor is on because a send turned it on. For tests and the trace.</summary>
        internal bool MonitorOnForASend => _sendMonitor?.OnForASend == true;

        /// <summary>
        /// The serial of a radio whose monitor a send turned on and could not
        /// put back, because the connection went first; the next connect to it
        /// puts it back (#835, Codex round 1, finding 3). Null when none.
        /// </summary>
        private string? _monitorPutBackPendingSerial;

        /// <summary>For tests: the radio a lost monitor put-back is waiting for.</summary>
        internal string? MonitorPutBackPendingSerial => Volatile.Read(ref _monitorPutBackPendingSerial);

        /// <summary>
        /// What a send holds on the radio, ended on the disconnect path while
        /// the link is still up (#835, Codex round 1, findings 3 and 4): the PTT
        /// source watch is revoked, and a monitor a send turned on is put back
        /// off now, by a command the radio acknowledges, rather than by the
        /// send's release later, which would find the connection gone. If the
        /// radio does not acknowledge within a second, the put-back is recorded
        /// for the next connect instead. Called from <see cref="Disconnect"/>.
        /// </summary>
        internal void RevokeSendHoldsForDisconnect(string why)
        {
            RevokePttWatch(why);
            Radio? radio = theRadio;
            SendMonitorOwner? owner = _sendMonitor;
            if (radio == null || owner == null || !owner.TakeBackForDisconnect()) return;
            bool acknowledged = false;
            using var answered = new ManualResetEventSlim();
            string refusal = SendRadioCommandWithReply(radio, "transmit set mon=0", reply =>
            {
                acknowledged = reply.Acknowledged;
                answered.Set();
            });
            if (refusal == null && answered.Wait(MonitorPutBackWaitMs) && acknowledged)
            {
                Tracing.TraceLine("SendMonitor: transmit monitor back off before the disconnect, acknowledged",
                    TraceLevel.Info);
                return;
            }
            Tracing.TraceLine("SendMonitor: the monitor's put-back before the disconnect was "
                + (refusal ?? (acknowledged ? "late" : "not acknowledged")) + "; noted for the next connect",
                TraceLevel.Warning);
            Volatile.Write(ref _monitorPutBackPendingSerial, radio.Serial);
        }

        /// <summary>How long the disconnect waits for the radio to acknowledge the monitor's put-back.</summary>
        private const int MonitorPutBackWaitMs = 1000;

        /// <summary>
        /// A connection to a radio whose monitor a send left on is up: put it
        /// back off (#835, Codex round 1, finding 3). By the radio's own command
        /// on the command queue, so it goes out in order with the connect's other
        /// work; FlexLib's cached TXMonitor may not be reported yet, and its
        /// setter skips a value it believes unchanged. Traced either way.
        /// </summary>
        private void PutBackLostSendMonitor(Radio? radio)
        {
            string? serial = radio?.Serial;
            if (serial == null) return;
            string? pending = Volatile.Read(ref _monitorPutBackPendingSerial);
            if (!string.Equals(pending, serial, StringComparison.OrdinalIgnoreCase)) return;
            Volatile.Write(ref _monitorPutBackPendingSerial, null);
            Tracing.TraceLine("SendMonitor: a send left this radio's monitor on at the last disconnect; putting it back off",
                TraceLevel.Info);
            q.Enqueue((FunctionDel)(() =>
            {
                if (!ReferenceEquals(theRadio, radio)) return;
                string refused = SendRadioCommandWithReply(radio, "transmit set mon=0", reply =>
                    Tracing.TraceLine("SendMonitor: monitor put-back after reconnect " + reply, TraceLevel.Info));
                if (refused != null)
                    Tracing.TraceLine("SendMonitor: monitor put-back after reconnect not sent: " + refused, TraceLevel.Warning);
            }), "SendMonitorPutBack");
        }

        private sealed class SendMonitorPort : SendMonitorOwner.IPort
        {
            private readonly FlexBase _rig;
            private Radio? _watched;
            private string? _watchedSerial;

            internal SendMonitorPort(FlexBase rig) { _rig = rig; }

            public bool? MonitorOn
            {
                get
                {
                    Radio? radio = _rig.theRadio;
                    return radio == null || !_rig.IsConnected ? null : radio.TXMonitor;
                }
            }

            public long OperatorWrites => System.Threading.Interlocked.Read(ref _rig._monitorOperatorWrites);

            public object? Connection
            {
                get
                {
                    Radio? radio = _rig.theRadio;
                    if (radio == null || !_rig.IsConnected) return null;
                    try { return (object?)radio.CurrentCommandConnection ?? radio; }
                    catch { return radio; }
                }
            }

            public void WriteMonitor(bool on)
            {
                Radio? radio = _rig.theRadio;
                if (radio == null) return;
                // The same queue and the same FlexLib property the Monitor
                // setter writes, but not through that setter: this write is
                // the send's, so it neither counts as the operator's nor says
                // anything.
                _rig.q.Enqueue((FunctionDel)(() =>
                {
                    if (ReferenceEquals(_rig.theRadio, radio)) radio.TXMonitor = on;
                }), "SendMonitor");
            }

            public void Watch(bool watching)
            {
                Radio? old = _watched;
                _watched = null;
                if (old != null) old.PropertyChanged -= OnRadioProperty;
                if (!watching) return;
                Radio? radio = _rig.theRadio;
                if (radio == null) return;
                _watched = radio;
                _watchedSerial = radio.Serial;
                radio.PropertyChanged += OnRadioProperty;
            }

            public void PutBackLost()
            {
                if (_watchedSerial != null) Volatile.Write(ref _rig._monitorPutBackPendingSerial, _watchedSerial);
            }

            private void OnRadioProperty(object? sender, PropertyChangedEventArgs e)
            {
                if (e.PropertyName != nameof(Radio.TXMonitor)) return;
                _rig._sendMonitor?.Observe();
            }
        }
    }
}
