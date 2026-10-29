#nullable enable
using System;
using System.ComponentModel;
using System.Diagnostics;
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

        private sealed class SendMonitorPort : SendMonitorOwner.IPort
        {
            private readonly FlexBase _rig;
            private Radio? _watched;

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
                radio.PropertyChanged += OnRadioProperty;
            }

            private void OnRadioProperty(object? sender, PropertyChangedEventArgs e)
            {
                if (e.PropertyName != nameof(Radio.TXMonitor)) return;
                _rig._sendMonitor?.Observe();
            }
        }
    }
}
